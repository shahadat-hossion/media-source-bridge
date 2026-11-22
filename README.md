# Media Source Bridge for Jellyfin

<p align="center">
  <img src="assets/github-preview.jpg" alt="Media Source Bridge for Jellyfin banner featuring a blue-lit cinema">
</p>

<p align="center">
  <a href="https://github.com/shahadat-hossion/media-source-bridge/releases/latest"><img src="https://img.shields.io/github/v/release/shahadat-hossion/media-source-bridge?label=latest%20release" alt="Latest release"></a>
  <a href="https://github.com/shahadat-hossion/media-source-bridge/actions/workflows/build.yml"><img src="https://github.com/shahadat-hossion/media-source-bridge/actions/workflows/build.yml/badge.svg" alt="Build status"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/shahadat-hossion/media-source-bridge" alt="MIT license"></a>
  <img src="https://img.shields.io/badge/Jellyfin-12.0.0-00A4DC?logo=jellyfin" alt="Jellyfin 12.0.0">
</p>

**Search private FTP, HTTP, or HTTPS media servers and add movies, TV shows, web series, and anime to Jellyfin as `.strm` files or administrator-downloaded local media.**

Media Source Bridge is a Jellyfin 12 plugin for media indexes hosted by a local ISP, home server, NAS, or another private-network server. Signed-in Jellyfin users can search those configured sources from a PC, TV, or phone and add a title to an existing Jellyfin library without manually browsing directories or copying links.

By default, the plugin creates small `.strm` pointer files. Jellyfin relays HTTP and HTTPS media from the configured private source through a signed loopback URL. FTP pointers still use the original FTP URL. Administrators can instead explicitly download the actual video files into the selected Jellyfin library.

> [!IMPORTANT]
> FTP and HTTP media-library sources must use loopback or a private IPv4 address (`10.x.x.x`, `172.16-31.x.x`, or `192.168.x.x`). The Jellyfin server must be able to route to each source.

## Features

- Search multiple private FTP, HTTP, and HTTPS directory indexes from Jellyfin.
- Search configured torrent providers from Jellyfin and identify matching titles with a **Torrent** badge.
- Paste a direct media file or folder link from a configured source and analyze it before adding.
- Keep title search and direct-link importing on separate, focused Jellyfin pages.
- Separate searches for **Movies**, **Shows**, and **Anime**.
- Configure a shared **Movies & TV Series (Both)** source.
- Browse poster-card results and preview the playable files found in a title folder.
- Create organized `.strm` files in any compatible, writable Jellyfin library.
- Let administrators download actual movies, series episodes, and anime episodes into the selected library folder.
- Preserve season subfolders and remote series structure where available.
- Skip existing destination files instead of overwriting them.
- Test one source or all configured sources from the plugin page.
- Export all configured sources to a portable JSON file and import them on another server. Credentials are excluded by default and can be included explicitly.
- Use optional FTP credentials stored in Jellyfin's plugin configuration.
- Let regular signed-in users search and add media while keeping source management administrator-only.
- Discover trending, new, and top-rated movies or series through TMDb.
- Discover trending anime through AniList.
- Preview exact private-server matches or add them automatically on a configurable schedule.
- Remember administrator-deleted automated titles so they are not added again.

Supported video extensions include `.mkv`, `.mp4`, `.avi`, `.mov`, `.m4v`, `.ts`, `.m2ts`, `.webm`, `.wmv`, `.mpg`, and `.mpeg`.

### Experimental torrent playback check

Jellyfin administrators can check a BitTorrent v1 magnet link under **Import Link → Torrent playback check**. On the Jellyfin host, install Node.js 22 or newer and FFprobe, then run `npm ci` and `npm start` from `scripts/torrent-bridge`. For a manually started bridge, set the same persistent `TORRENT_BRIDGE_SECRET` (at least 32 bytes) in the environments of both Jellyfin and the bridge. The managed bridge generates and retains its own secret. Previously generated unsigned torrent STRM files need to be prepared again. If FFprobe is outside PATH, set `TORRENT_FFPROBE` to its executable path before starting the bridge. The bridge and Jellyfin must share loopback access.

The GitHub release includes a Windows x64 torrent bridge ZIP with Node.js and dependencies, plus a source ZIP for other platforms. On Windows x64, stop Jellyfin, extract the bundle into its program data directory, and restart Jellyfin. The plugin starts the bridge automatically.

For Jellyfin-managed startup on other platforms, place the bridge files and installed Node dependencies in `torrent-bridge` under Jellyfin's program data directory. Put the Node executable in `torrent-bridge/runtime` as `node.exe` on Windows or `node` on Linux. Jellyfin starts and supervises that installation. Use one launcher for the bridge.

The bridge discovers public peers through DHT, selects the largest supported video, times the first 4 MiB (or the entire file if smaller), and estimates the average video bitrate from its duration and size. **Likely smooth** requires the full opening sample and at least 50% throughput headroom. **May buffer** flags insufficient headroom or an unknown bitrate. **Unavailable** covers cases such as missing metadata, an empty opening sample, and torrents over the RAM limit. These checks measure the torrent source at startup. Playback can also depend on the client and Jellyfin transcoding.

**Import Link** also offers **Save Test STRM** for manual testing. Automatic torrent imports use a durable URL that reloads the torrent on demand. The bridge holds one torrent at a time, with a RAM budget of up to 4 GiB based on the host's memory at startup. Jellyfin may write temporary transcoding output if the client requires a transcode.

Run `npm test` from the bridge directory to test the HTTP check and byte-range playback with a local seed. Set `TORRENT_TEST_VIDEO` to an MP4 of at least 2 KiB to include real FFprobe metadata in that test.

### Search with torrent providers

In **Setup Media → Torrent Providers → Browse Providers**, search the built-in catalog or filter it by media type. Use **Test** to check a provider from the Jellyfin server, then **Add**. The built-in entries need no credentials. Knaben covers movies, shows, and anime. The 1337x, The Pirate Bay, and YTS entries use Knaben with an individual source filter. Nyaa covers anime.

Enable **18+ torrent mode** under **Setup Media → Source Availability** to show the **Adult torrents** search filter for administrators. This filter searches configured adult-capable providers such as 0Magnet and custom Torznab providers that support adult categories. Adult results appear only in this filter.

Use **Setup Media → Source Availability** to pause configured media servers (FTP, HTTP, and HTTPS) and torrent sources independently. Both switches save immediately and retain the configured servers and providers. Pausing media servers excludes them from search, import, automation, and signed HTTP playback. Pausing torrents stops provider searches and new playback checks. Existing Jellyfin library items remain searchable, and you can still run connection tests while either source type is paused.

To use an additional Torznab indexer, open **Add Custom Torznab** and enter its name, API endpoint, and API key if required. Enter the key in its own field. Use **Test connection** to verify search access, then **Add provider**.

Signed-in users can search enabled providers through **Search & Add Media**. **Torrent** badges open the release list with provider, resolution, file size, and seeder count when supplied. Matching library or media server cards carry the badge. Other matches appear under **Torrent providers**.

**Experimental torrent playback.** When an administrator selects **Open** on a movie, show, or anime result tagged **Torrent**, the bridge tests up to four compatible releases in parallel and selects one that fills the opening video buffer. The plugin then creates STRM files and opens the indexed title. The bridge discovers peers through DHT and public trackers. Automatic import supports releases with a compatible magnet that fit the bridge's RAM budget.

## Requirements

- Jellyfin Server **12.0.0** with plugin ABI `12.0.0.0`.
- An administrator for repository installation and private-source configuration.
- Network access from the Jellyfin server to each configured media source.
- An existing Movies or Shows library with a folder writable by the Jellyfin process.
- A signed-in Jellyfin account to search and add media.
- Permission to access and use the media supplied by the private server.

## Install through the Jellyfin plugin catalog

1. In Jellyfin, open **Dashboard > Plugins > Repositories**.
2. Select **Add Repository**.
3. Use `Media Source Bridge` as the repository name.
4. Paste this manifest URL:

   ```text
   https://raw.githubusercontent.com/shahadat-hossion/media-source-bridge/main/manifest.json
   ```

5. Save, then open **Dashboard > Plugins > Catalog**.
6. Select **Media Source Bridge** and choose **Install**.
7. Restart Jellyfin.

If you previously installed Local ISP FTP Loader, update the existing repository entry to the manifest URL above. The plugin keeps the same ID.

If the plugin is missing from the catalog, open the manifest URL from the Jellyfin server and confirm the server is running Jellyfin `12.0.0`.

## Configure a private media source

An administrator performs this once for each server:

1. Open **Setup Media** from the Media Source Bridge landing page.
2. Enter a display name, media type, and complete `ftp://`, `http://`, or `https://` directory URL.
3. Add FTP credentials if the server requires them.
4. Select **Test connection**.
5. Select **Add server** after the test succeeds.

Choose **Movies & TV Series (Both)** when one server contains both categories. Source configuration, connection tests, saved credentials, and source removal are visible only to Jellyfin administrators.

The plugin does not include provider-specific addresses. Each administrator supplies the private server URLs available on their own network.

## Search and add media

1. Select the **Search & Add Media** icon in the Jellyfin web header or open **Search** from the Media Source Bridge landing page.
2. Enter at least two characters from a movie, show, episode, or anime title.
3. Review the single **Search Results** grid. Matching library items appear first, followed by titles available from configured media servers.
4. Duplicate titles are collapsed into one card. Library items show **Play** (or **Open** for a series); titles not yet in Jellyfin show **Add & Play**.
5. Select **Add & Play** to create the `.strm` in the configured default save location, refresh that library, and open the indexed title. No destination popup is shown.

### Default save locations

An administrator can open **Setup Media** and assign one default Jellyfin library for Movies, Shows, and Anime. These shared defaults are used by Discover, home Search, Search Media, Import Link, Add Media, STRM creation, and downloads. Changing a default there changes the destination for every workflow.
6. The plugin creates the lightweight `.strm` files, starts a Jellyfin scan, waits for the indexed item, and then plays the movie or opens the show. If indexing exceeds 90 seconds, it reports that the title was added and lets Jellyfin finish in the background.

Administrators can expand **Search button settings** on the unified search page to keep Jellyfin's standard search button or replace it with the plugin search. The plugin search icon is always labelled **Search & Add Media** for accessibility.

Jellyfin handles metadata, artwork, transcoding, and playback normally. The remote server must remain reachable whenever a stream is played.

## Automatic discovery

Open **Media Source Bridge** in Jellyfin's sidebar to reach the landing page and expand its links to **Discover**, **Search**, **Import Link**, and administrator-only **Setup Media**. Select the sidebar entry again to collapse the links. Discover displays responsive Movies, Shows, and Anime grids for signed-in users, with animated skeleton cards and progress indicators during API and private-server operations. Administrators can expand **Automation settings** on that page and configure:

- A [TMDb API key or read-access token](https://www.themoviedb.org/settings/api) for movie and series lists. The page includes a **Get TMDb API key** button that opens this official account page in a new tab.
- Any combination of trending, new, and top-rated movies or series.
- Trending anime from AniList; no AniList credential is required.
- A run interval, per-category limit, minimum rating, and minimum TMDb vote count. Discover uses the shared Default Save Locations from Setup Media.
- **Preview only** mode to inspect exact matches without creating files.

The discovery providers supply titles and metadata only. The plugin searches the configured private sources and creates `.strm` files only when one exact title/year match is found. Missing and ambiguous matches are skipped. Scheduled runs check every 15 minutes and execute when the configured interval is due.

Automatically added titles and their generated files are tracked. If an administrator deletes all tracked `.strm` files for a title, the next run places its provider ID in **Excluded titles** instead of restoring it. An administrator can select **Allow again** to remove that exclusion.

> [!NOTE]
> This product uses the TMDB API but is not endorsed or certified by TMDB. Anime discovery data is provided by AniList.

## How it works

```text
Private FTP/HTTP index
        ↓ search and inspect
Media Source Bridge
        ↓ create .strm pointers or download actual files (admin)
Existing Jellyfin library
        ↓ normal library scan
Jellyfin metadata and playback
```

Movies are placed in a title folder. Series keep useful remote subfolders such as seasons when available. File and directory names are sanitized before local files are created, and an existing destination file is never overwritten.

## Troubleshooting

<details>
<summary><strong>Search returns no results</strong></summary>

- Ask an administrator to run **Test connection** or **Test all servers**.
- Try a shorter title without a release year or punctuation.
- Confirm that the correct Movies, Shows, or Anime tab is selected.
- Check whether the provider renamed, moved, or temporarily disabled the remote folder.
</details>

<details>
<summary><strong>No default save location appears</strong></summary>

- Create the Movies or Shows library in Jellyfin first.
- Confirm that it has a local folder path writable by the Jellyfin process.
- Refresh **Add Media** after changing Jellyfin libraries.
</details>

<details>
<summary><strong>Media was added but does not appear</strong></summary>

- Run **Dashboard > Scheduled Tasks > Scan Media Library**.
- Verify that Jellyfin can write to the selected library folder.
- Open the generated `.strm` file and confirm its URL is reachable from the Jellyfin server.
- Check the Jellyfin log for library scan or remote playback errors.
</details>

<details>
<summary><strong>A stream does not play</strong></summary>

The `.strm` file points directly to the configured private server. Confirm that the remote file still exists, the private server is online, and the Jellyfin server can reach it. The plugin cannot restore an unavailable remote file.
</details>

<details>
<summary><strong>An older interface is still visible after updating</strong></summary>

Restart Jellyfin, close and reopen **Add Media**, then hard-refresh the web client with **Ctrl + F5**. Version `2.4.1` or newer is required for the combined Movies and TV source type.
</details>

## Security and privacy

- Plugin actions require Jellyfin authentication. The playback relay accepts signed URLs over loopback, and static client assets are public.
- Source-management operations require Jellyfin administrator elevation.
- Only loopback and private IPv4 source URLs are accepted.
- A source URL must remain inside a configured private root. The plugin rejects HTTP redirects for listings, downloads, and relayed playback. Re-import an existing HTTP STRM to upgrade its matching direct pointer.
- Configure Movies, Shows, and Anime under **Setup Media → Default Save Locations**. Each destination must match a current compatible Jellyfin library.
- Re-importing upgrades a matching legacy STRM pointer. Existing media and pointers to different sources are preserved.
- Users can list and delete their own media requests. Requests created before ownership was recorded are visible to administrators.
- Actual media downloads require Jellyfin administrator elevation and can only target a current compatible library path.
- FTP credentials remain in Jellyfin's plugin configuration.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), clone this repository, and run:

```powershell
.\build.ps1
```

The build creates a release ZIP and MD5 checksum in `dist`. The ZIP contains the plugin DLL, dependency manifest, and `meta.json`.

## Limitations

- No IMDb, TMDb, Google, torrent, or public-internet media search.
- Downloads run on the Jellyfin server with one active transfer and up to four queued downloads. Administrators can cancel from the download progress panel. A transfer is limited to 50 GiB, six hours overall, and 30 seconds of inactivity. Completed files are kept on cancellation. Temporary download files are excluded from imports and cleaned up on cancellation or a later retry.
- No automatic Jellyfin library creation or reconfiguration.
- No anonymous access or public FTP/HTTP roots.
- No ability to bypass ISP routing or make a private server reachable from another network.

## License

Media Source Bridge is released under the [MIT License](LICENSE).
