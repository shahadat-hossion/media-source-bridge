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

## Quick start

1. [Install the plugin](#install-through-the-jellyfin-plugin-catalog) as a Jellyfin administrator.
2. [Create a library and set its save location](#create-a-library-and-set-its-save-location).
3. [Connect a private media source](#configure-a-private-media-source).
4. [Search for a title](#search-and-add-media) or [import a known file or folder](#import-a-file-or-folder).

For torrent playback, also [install the matching torrent bridge](#install-the-torrent-bridge). The bridge is optional for private media and torrent provider search.

## Features

- Search multiple private FTP, HTTP, and HTTPS directory indexes from Jellyfin.
- Search TV and anime by series title, with typo-tolerant matching for private media sources.
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
7. Restart the Jellyfin server, reopen the web client, and select **Media Source Bridge** in the sidebar. **Setup Media** is available to administrators.

If you previously installed Local ISP FTP Loader, update the existing repository entry to the manifest URL above. The plugin keeps the same ID.

If the plugin is missing from the catalog, verify that the Jellyfin server can reach the manifest and supports plugin ABI `12.0.0.0`.

<details>
<summary>Manual installation from the GitHub release</summary>

1. Download `Media-Source-Bridge-<version>.zip` from the [GitHub release](https://github.com/shahadat-hossion/media-source-bridge/releases/latest). The release also provides an MD5 checksum for the plugin ZIP.
2. Stop Jellyfin. Locate its `plugins` directory and create a folder for this version of the plugin, such as `MediaSourceBridge_2.7.10.0` for v2.7.10.
3. Extract the ZIP into that folder so `Jellyfin.Plugin.LinkAdder.dll` and `meta.json` are directly inside it. Preserve the settings in `plugins/configurations` if you are updating.
4. Give the Jellyfin service account permission to read the extracted files, start Jellyfin, and confirm that **Media Source Bridge** appears under **Dashboard → Plugins**.

The plugin path depends on how Jellyfin is installed. [Jellyfin plugin installation documentation](https://jellyfin.org/docs/general/server/plugins/)

</details>

## Create a library and set its save location

1. Open **Dashboard → Libraries**. Create a Movies or Shows library if you do not already have one. For anime, use a Shows library with **Anime** in the name.
2. Add a folder the Jellyfin process can write to, such as `D:\Media\Movies` on Windows. If Jellyfin runs in a container, mount the host folder read-write and enter the path from inside the container, such as `/media/movies`.
3. Open **Media Source Bridge → Setup Media → Default Save Locations**. Choose a library for each media type you plan to use and select **Save default locations**.

Search, Import Link, Discover, and administrator downloads use these shared defaults. Jellyfin indexes the added files through its normal library scan.

## Configure a private media source

An administrator performs this once for each server:

1. Open **Setup Media** from the Media Source Bridge landing page.
2. Under **Add Server**, enter a display name, media type, and full directory URL ending in `/`. Examples include `ftp://192.168.1.20/Anime/`, `http://192.168.1.20:8080/Movies/`, or `https://192.168.1.20/Shows/`.
3. If the source requires credentials, use the separate username and password fields. For HTTPS, use a certificate that the Jellyfin server trusts and that is valid for the IP address.
4. Select **Test connection**.
5. Select **Add server** after the test succeeds.

Choose **Movies & TV Series (Both)** when one server contains both categories. Source configuration, connection tests, saved credentials, and source removal are visible only to Jellyfin administrators.

For HTTP or HTTPS search, the endpoint must list links to media files or folders. Use the final directory URL because the plugin rejects HTTP redirects. In Docker, `127.0.0.1` refers to the Jellyfin container itself. Use a reachable private IP if the media server runs elsewhere.

Use **Configured Servers → Test all servers** when troubleshooting several sources. **Export servers** and **Import servers** let you transfer the list as JSON. Exports omit credentials by default.

## Search and add media

1. Select the **Search & Add Media** icon in the Jellyfin web header or open **Search** from the Media Source Bridge landing page.
2. Enter at least two characters from a movie, show, or anime title. TV and anime series appear as series cards. Open a card to browse its episodes.
3. Review the single **Search Results** grid. Matching library items appear first, followed by titles available from configured media servers.
4. Choose **Play** or **Open** for an item already in your library. For a private-source result, select the **FTP** add action. The same action supports configured HTTP and HTTPS sources.
5. The plugin creates the STRM in the default save location and queues a library scan. Once indexed, it plays the movie or opens the show. If indexing takes more than 90 seconds, select **Check again** once Jellyfin has had time to finish.

Administrators can expand **Search button settings** on the unified search page to keep Jellyfin's standard search button or replace it with the plugin search. The plugin search icon is always labelled **Search & Add Media** for accessibility.

Private media search ranks exact titles and partial matches first, then checks for a single typo in longer words. It indexes alphabetized TV and anime folders so titles inside those folders can appear in search.

### Set a search limit

Open **Setup Media → Search limit** as a Jellyfin administrator. Enter the maximum requests per minute for each user and select **Save search limit**. Use **0** for unlimited search, which is the default. Media server and torrent requests share the same allowance, while local Jellyfin library searches and imports have separate handling. One search can use two requests when both remote sources are enabled.

Jellyfin handles metadata, artwork, transcoding, and playback normally. The remote server must remain reachable whenever a stream is played.

## Import a file or folder

1. Open **Media Source Bridge → Import Link** and select the media type.
2. Paste a supported video file or folder URL that is inside one of your saved sources, then select **Analyze Link**.
3. Review the file list, detected seasons, and destination. Adjust the title if needed and select **Create STRM Files**.
4. When Jellyfin finishes its scan, open the item from your library.

### Download actual media files

As an administrator, analyze the link using the same steps and select **Download Files**. Leave the page open to watch progress or use **Cancel Download**. You can also leave after starting the job and let it continue on the server. Completed files remain in the destination folder if you cancel the remaining transfer. After a completed download, you can play the saved video even if the original source is offline.

## Install the torrent bridge

Install the Jellyfin plugin first, then choose the bridge asset from the same [GitHub release](https://github.com/shahadat-hossion/media-source-bridge/releases/latest). Updating the plugin through Jellyfin's catalog leaves the bridge installation to you.

In the Jellyfin startup log, find the line labelled **Program data path**. That's where the `torrent-bridge` folder belongs, alongside `plugins`. Common paths include the following. Use the path from your own startup log if it differs. [Jellyfin server paths](https://jellyfin.org/docs/general/administration/configuration/)

| Installation | Common program data path |
| --- | --- |
| Windows service or tray | `C:\ProgramData\Jellyfin\Server` |
| Windows direct launch | `%LOCALAPPDATA%\jellyfin` |
| Debian or Ubuntu package | `/var/lib/jellyfin` |
| Official Jellyfin container | `/config` inside the container |

### Windows x64 bundle

1. Download `Torrent-Bridge-<version>-windows-x64.zip`. It includes Node.js and the installed bridge dependencies.
2. Stop the Jellyfin server and any manually started bridge. Extract the ZIP into Jellyfin's program data directory. The extracted files should be laid out like this.

   ```text
   <Jellyfin program data>/
   ├── plugins/
   └── torrent-bridge/
       ├── bridge.mjs
       ├── node_modules/
       └── runtime/
           └── node.exe
   ```

3. Start Jellyfin. The plugin starts the bridge and supplies a persistent signing secret. Look for **Torrent playback check listening** in the Jellyfin log.
4. The managed bridge uses FFprobe from the Jellyfin executable directory when available. Otherwise, put FFprobe on the Jellyfin service account's PATH or set `TORRENT_FFPROBE` to its full path in the service environment.

### Linux, Docker, and other platforms

1. Download `Torrent-Bridge-<version>-source.zip` and extract it into the program data directory. For Docker, use the host volume that maps to the program data path inside the container.
2. Install Node.js 22 or later, npm, and FFprobe for the operating system and CPU that run Jellyfin. Run `npm ci` in the extracted `torrent-bridge` directory. For a container, build the native dependencies in an environment compatible with that container.
3. For Jellyfin-managed startup, place the compatible Node executable at `torrent-bridge/runtime/node` on Linux or `torrent-bridge/runtime/node.exe` on Windows. Give the Jellyfin service account access to read the bridge files and execute Node, then restart Jellyfin. For example, on a standard Debian or Ubuntu installation with Node.js already installed:

   ```sh
   cd /var/lib/jellyfin/torrent-bridge
   npm ci
   mkdir -p runtime
   install -m 755 "$(command -v node)" runtime/node
   ```

For manual startup, leave the managed runtime path unused. Set the same persistent `TORRENT_BRIDGE_SECRET` of at least 32 bytes in the environments of Jellyfin and the bridge, then run `npm start` in the bridge directory. Keep both processes on the same loopback network. The default bridge API and internal stream ports are `127.0.0.1:18781` and `127.0.0.1:18782`. Run one bridge per port pair.

When upgrading, update the plugin and bridge together. Retain the plugin configuration and any manually configured secret. Re-prepare torrent STRM files created before signed playback was introduced.

### Check a torrent magnet

As an administrator, open **Media Source Bridge → Import Link → Torrent playback check**. Paste a BitTorrent v1 magnet, then select **Check Playback**. Select **Save Test STRM** if you want to save the signed pointer that the check returns.

The bridge discovers public peers through DHT, selects the largest supported video, times the first 4 MiB (or the entire file if smaller), and estimates the average video bitrate from its duration and size. **Likely smooth** requires the full opening sample and at least 50% throughput headroom. **May buffer** flags insufficient headroom or an unknown bitrate. **Unavailable** covers cases such as missing metadata, an empty opening sample, and torrents over the RAM limit. These checks measure the torrent source at startup. Playback can also depend on the client and Jellyfin transcoding.

Torrent imports use signed URLs that can reload the torrent on demand. The bridge holds one torrent at a time, with a RAM budget of up to 4 GiB based on the host's memory at startup. Jellyfin may write temporary output if the client requires transcoding.

Run `npm test` from the bridge directory to test the HTTP check and byte-range playback with a local seed. Set `TORRENT_TEST_VIDEO` to an MP4 of at least 2 KiB to include real FFprobe metadata in that test.

## Search with torrent providers

In **Setup Media → Torrent Providers → Browse Providers**, search the built-in catalog or filter it by media type. Use **Test** to check a provider from the Jellyfin server, then **Add**. The built-in entries need no credentials. Knaben covers movies, shows, and anime. The 1337x, The Pirate Bay, and YTS entries use Knaben with an individual source filter. Nyaa covers anime.

Enable **18+ torrent mode** under **Setup Media → Source Availability** to show the **Adult torrents** search filter for administrators. This filter searches configured adult-capable providers such as 0Magnet and custom Torznab providers that support adult categories. Adult results appear only in this filter.

Use **Setup Media → Source Availability** to pause configured media servers (FTP, HTTP, and HTTPS) and torrent sources independently. Both switches save immediately and retain the configured servers and providers. Pausing media servers excludes them from search, import, automation, and signed HTTP playback. Pausing torrents stops provider searches and new playback checks. Existing Jellyfin library items remain searchable, and you can still run connection tests while either source type is paused.

To use an additional Torznab indexer, open **Add Custom Torznab** and enter its name, API endpoint, and API key if required. Enter the key in its own field. Use **Test connection** to verify search access, then **Add provider**.

Signed-in users can search enabled providers through **Search & Add Media**. **Torrent** badges open the release list with provider, resolution, file size, and seeder count when supplied. Matching library or media server cards carry the badge. Other matches appear under **Torrent providers**.

**Experimental torrent playback.** When an administrator selects **Open** on a movie, show, or anime result tagged **Torrent**, the bridge tests up to four compatible releases in parallel and selects one that fills the opening video buffer. The plugin then creates STRM files and opens the indexed title. The bridge discovers peers through DHT and public trackers. Automatic import supports releases with a compatible magnet that fit the bridge's RAM budget.

## Automatic discovery

Open **Media Source Bridge** in Jellyfin's sidebar to reach the landing page and expand its links to **Discover**, **Search**, **Import Link**, and administrator-only **Setup Media**. Select the sidebar entry again to collapse the links. Discover displays responsive Movies, Shows, and Anime grids for signed-in users, with animated skeleton cards and progress indicators during API and private-server operations. Administrators can expand **Automation settings** on that page and configure:

- A [TMDb API key or read-access token](https://www.themoviedb.org/settings/api) for movie and series lists. The page includes a **Get TMDb API key** button that opens this official account page in a new tab.
- Any combination of trending, new, and top-rated movies or series.
- Trending anime from AniList; no AniList credential is required.
- A run interval, per-category limit, minimum rating, and minimum TMDb vote count. Discover uses the shared Default Save Locations from Setup Media.
- **Preview only** mode to inspect exact matches without creating files.

The discovery providers supply titles and metadata only. The plugin searches the configured private sources and creates `.strm` files only when one exact title/year match is found. Missing and ambiguous matches are skipped. Scheduled runs check every 15 minutes and execute when the configured interval is due.

To try Discover without scheduling imports, open a card and select **Add to Library**. If it returns multiple candidates, choose the correct match. To set up automation, choose your categories and filters, leave **Preview only** enabled, select **Save**, then **Preview matches**. When the results look right, clear **Preview only**, save, and select **Run now** or enable scheduled runs. Review the results and history on the same page.

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

Movies are placed in a title folder. Series are grouped into season folders when the source provides usable season or episode labels. Re-importing can upgrade a matching legacy STRM pointer. Existing media files and pointers to other sources are preserved.

## Troubleshooting

Use **Report a problem** in the upper-right of Search to open the project's [GitHub issue form](https://github.com/shahadat-hossion/media-source-bridge/issues/new). Include the plugin version, what you searched for, and what you expected to see. Remove credentials and private server addresses from screenshots or logs before posting.

<details>
<summary><strong>Search returns no results</strong></summary>

- Ask an administrator to run **Test connection** or **Test all servers**.
- Try a shorter title without a release year or punctuation.
- Confirm that the correct Movies, Shows, or Anime tab is selected.
- Check whether the provider renamed, moved, or temporarily disabled the remote folder.
- If you see **Search request limit reached**, wait briefly or ask an administrator to check **Setup Media → Search limit**.
</details>

<details>
<summary><strong>No default save location appears</strong></summary>

- Create the Movies or Shows library in Jellyfin first.
- Confirm that it has a local folder path writable by the Jellyfin process.
- Open **Setup Media → Default Save Locations** and save the new default, then reopen the Search or Import Link page.
</details>

<details>
<summary><strong>Media was added but does not appear</strong></summary>

- Run **Dashboard > Scheduled Tasks > Scan Media Library**.
- Verify that Jellyfin can write to the selected library folder.
- Confirm that the signed HTTP playback URL or the direct FTP URL is reachable from the Jellyfin server.
- Verify that the signed-in user has access to the destination library.
- Check the Jellyfin log for library scan or remote playback errors.
</details>

<details>
<summary><strong>A stream does not play</strong></summary>

Confirm that the source still exists, is enabled in **Source Availability**, and is reachable from Jellyfin. Current HTTP and HTTPS pointers use a signed loopback relay, while FTP pointers use their original FTP URL. Re-import an older direct HTTP or HTTPS STRM to upgrade it. For torrent pointers, verify the bridge is running. Re-prepare torrents that were added before signed playback.

</details>

<details>
<summary><strong>The torrent bridge will not start or reports an unknown bitrate</strong></summary>

- Confirm the plugin and bridge came from the same release. Check that you extracted `torrent-bridge` directly under the program data directory from the Jellyfin startup log.
- Check the Jellyfin log for bridge startup errors. An address-in-use error on port `18781` or `18782` means a listener already owns that port. Check for another copy of the bridge.
- For a manually started bridge, verify that both processes use the same secret and share the same loopback network. In Docker, an ordinary separate container has its own loopback interface.
- If bitrate is unknown, check whether FFprobe is available to the bridge. If a torrent buffers after a successful check, peer availability or transcoding can still be a factor.
</details>

<details>
<summary><strong>An older interface is still visible after updating</strong></summary>

Restart Jellyfin, hard-refresh the web client, then reopen the plugin page. If the sidebar entry is still missing, confirm the plugin is listed under **Dashboard → Plugins**.
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
- Saved source credentials remain in Jellyfin's plugin configuration.

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), clone this repository, and run:

```powershell
dotnet test tests/LinkAdder.Tests --configuration Release
.\build.ps1
```

The build creates a plugin release ZIP and MD5 checksum in `dist`. To test the torrent bridge, run `npm ci` and `npm test` in `scripts/torrent-bridge`. On Windows x64, run `.\scripts\Build-TorrentBridge.ps1` from the repository root after installing the bridge dependencies to build both bridge ZIPs.

## Limitations

- The private-source importer searches configured media roots. Discovery uses TMDb and AniList for metadata, and torrent search uses the providers you enable.
- Downloads run on the Jellyfin server with one active transfer and up to four queued downloads. Administrators can cancel from the download progress panel. A transfer is limited to 50 GiB, six hours overall, and 30 seconds of inactivity. Completed files are kept on cancellation. Temporary download files are excluded from imports and cleaned up on cancellation or a later retry.
- No automatic Jellyfin library creation or reconfiguration.
- No anonymous access or public FTP/HTTP roots.
- No ability to bypass ISP routing or make a private server reachable from another network.

## License

Media Source Bridge is released under the [MIT License](LICENSE).
