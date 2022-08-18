using System;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Text.Json;
using System.Net.Http.Headers; 

public class Updater
{
    private const string SEMVER_FINDER = @"\D*(\d(\.\d)*\.\d)\D*";

    private const string GITHUB_API_URL = "https://api.github.com/repos/{0}/{1}/releases";
    private static readonly string[] ZIP_TYPES = {"application/x-zip-compressed", "application/zip"};
    private const string ZIP_FILE_NAME = "core.zip";

    private bool _useConsole = false;
    private string _baseDir;
    private string _coresFile;

    public Updater(string coresFile, string baseDirectory)
    {
        _baseDir = baseDirectory;

        //make sure the json file exists
        if(File.Exists(coresFile)) {
            _coresFile = coresFile;
        } else {
            throw new FileNotFoundException("Cores json file not found: " + coresFile);
        }
    }

    public void PrintToConsole(bool set)
    {
        _useConsole = set;
    }

    public async Task RunUpdates()
    {
        string json = File.ReadAllText(_coresFile);
        List<Core>? coresList = JsonSerializer.Deserialize<List<Core>>(json);
        //TODO check if null
        foreach(Core core in coresList) {
            Repo repo = core.repo;
            _writeMessage("Starting Repo: " + repo.project);
            string name = core.name;
            bool allowPrerelease = core.allowPrerelease;
            string url = String.Format(GITHUB_API_URL, repo.user, repo.project);
            string response = await _fetchReleases(url);
            if(response == null) {
                Environment.Exit(1);
            }
            List<Github.Release>? releases = JsonSerializer.Deserialize<List<Github.Release>>(response);
            var mostRecentRelease = _getMostRecentRelease(releases, allowPrerelease);
            
            string tag_name = mostRecentRelease.tag_name;
            List<Github.Asset> assets = mostRecentRelease.assets;

            Regex r = new Regex(SEMVER_FINDER);
            Match matches = r.Match(tag_name);

            var releaseSemver = matches.Groups[1].Value;
            //TODO throw some error if it doesn't find a semver in the tag
            releaseSemver = _semverFix(releaseSemver);

            Github.Asset coreAsset = null;

            // might need to search for the right zip here if there's more than one
            //iterate through assets to find the zip release
            for(int i = 0; i < assets.Count; i++) {
                if(ZIP_TYPES.Contains(assets[i].content_type)) {
                    coreAsset = assets[i];
                    break;
                }
            }

            if(coreAsset == null) {
                _writeMessage("No zip file found for release. Skipping");
                continue;
            }

            string nameGuess = name ?? coreAsset.name.Split("_")[0];
            _writeMessage(tag_name + " is the most recent release, checking local core...");
            string localCoreFile = Path.Combine(_baseDir, "Cores/"+nameGuess+"/core.json");
            bool fileExists = File.Exists(localCoreFile);

              if (fileExists) {
                json = File.ReadAllText(localCoreFile);
                
                Analogue.Config? config = JsonSerializer.Deserialize<Analogue.Config>(json);
                Analogue.Core localCore = config.core;
                string ver_string = localCore.metadata.version;

                matches = r.Match(ver_string);
                string localSemver = "";
                if(matches != null && matches.Groups.Count > 1) {
                    localSemver = matches.Groups[1].Value;
                    localSemver = _semverFix(localSemver);
                    _writeMessage("local core found: v" + localSemver);
                }

                if (!_isActuallySemver(localSemver) || !_isActuallySemver(releaseSemver)) {
                    _writeMessage("downloading core anyway");
                    await _updateCore(coreAsset.browser_download_url);
                    continue;
                }

                if (_semverCompare(releaseSemver, localSemver)){
                    _writeMessage("Updating core");
                    await _updateCore(coreAsset.browser_download_url);
                } else {
                    _writeMessage("Up to date. Skipping core");
                }
            } else {
                _writeMessage("Downloading core");
                await _updateCore(coreAsset.browser_download_url);
            }
            _writeMessage("------------");
        }
    }

    private Github.Release _getMostRecentRelease(List<Github.Release> releases, bool allowPrerelease)
    {
        foreach(Github.Release release in releases) {
            if(!release.draft && (allowPrerelease || !release.prerelease)) {
                return release;
            }
        }

        return null;
    }

    private async Task<string> _fetchReleases(string url)
    {
        try {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Get,
                RequestUri = new Uri(url)
            };
            var agent = new ProductInfoHeaderValue("Analogue-Pocket-Auto-Updater", "1.0");
            request.Headers.UserAgent.Add(agent);
            var response = await client.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return responseBody;
        } catch (HttpRequestException e) {
            _writeMessage("Error pulling communicating with Github API.");
            _writeMessage(e.Message);
            return null;
        }
    }

    //even though its technically not a valid semver, allow use of 2 part versions, and just add a .0 to complete the 3rd part
    private string _semverFix(string version)
    {
        string[] parts = version.Split(".");

        if(parts.Length == 2) {
            version += ".0";
        }
        
        return version;
    }

    private bool _semverCompare(string semverA, string semverB)
    {
        Version verA = Version.Parse(semverA);
        Version verB = Version.Parse(semverB);
        
        switch(verA.CompareTo(verB))
        {
            case 0:
            case -1:
                return false;
            case 1:
                return true;
            default:
                return true;
        }
    }

    private bool _isActuallySemver(string potentiallySemver)
    {
        Version ver = null;
        return Version.TryParse(potentiallySemver, out ver);
    }

    private async Task _updateCore(string downloadLink)
    {
        _writeMessage("Downloading file " + downloadLink + "...");
        string zipPath = Path.Combine(_baseDir, ZIP_FILE_NAME);
        string extractPath = _baseDir;
        await HttpHelper.DownloadFileAsync(downloadLink, zipPath);

        _writeMessage("Extracting...");
        ZipFile.ExtractToDirectory(zipPath, extractPath, true);
        File.Delete(zipPath);
        _writeMessage("Installation complete.");
    }

    private void _writeMessage(string message)
    {
        if(_useConsole) {
            Console.WriteLine(message);
        }
    }
}