# Torrent bridge installation

Use a torrent bridge download from the same GitHub release as the Media Source Bridge plugin.

## Windows x64

1. Stop Jellyfin and any manually started torrent bridge.
2. Extract the Windows x64 ZIP into Jellyfin's program data directory. The resulting folder should contain `torrent-bridge/bridge.mjs`, `torrent-bridge/runtime/node.exe`, and `torrent-bridge/node_modules`.
3. Restart Jellyfin. The plugin starts the bridge and supplies its persistent signing secret.

The Windows bundle includes Node.js and the installed dependencies. The managed bridge uses Jellyfin's bundled FFprobe if available. Otherwise, put FFprobe on PATH or set `TORRENT_FFPROBE` in Jellyfin's environment.

## Linux and other platforms

Extract the source ZIP. Install Node.js 22 or later and FFprobe, then run `npm ci` inside the extracted `torrent-bridge` directory. For Jellyfin-managed startup, place the installed directory in Jellyfin's program data directory and put a compatible Node executable at `torrent-bridge/runtime/node` (or `node.exe` on Windows). Give it executable permissions where required, then restart Jellyfin.

For manual startup, set the same persistent `TORRENT_BRIDGE_SECRET` of at least 32 bytes in the environments of Jellyfin and the bridge, then run `npm start` in the bridge directory. Run only one bridge. It must share Jellyfin's loopback network.

Re-prepare torrent STRM files created before signed playback was introduced.
