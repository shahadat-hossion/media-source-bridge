import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import MemoryChunkStore from 'memory-chunk-store';
import WebTorrent from 'webtorrent';
import { startBridge } from './bridge.mjs';

const localOptions = { dht: false, tracker: false, lsd: false, natUpnp: false, natPmp: false };

const secret = 'integration-test-bridge-secret-32-bytes';

test('checks a local torrent, reports headroom and serves seekable video bytes', { timeout: 30_000 }, async t => {
    const fixture = process.env.TORRENT_TEST_VIDEO;
    const video = fixture ? await readFile(fixture) : Buffer.alloc(16 * 1024 * 1024, 0x37);
    if (!fixture) {
        for (let offset = 0; offset < video.length; offset += 4096) {
            video.writeUInt32LE(offset, offset);
        }
    }
    const unknownVideo = Buffer.alloc(64 * 1024, 0x41);
    const seeder = new WebTorrent(localOptions);
    t.after(() => new Promise(done => seeder.destroy(done)));
    const seeded = await new Promise(done => seeder.seed(video, { name: 'test.mp4', store: MemoryChunkStore }, done));
    const unknown = await new Promise(done => seeder.seed(unknownVideo, { name: 'unknown.mp4', store: MemoryChunkStore }, done));

    const clientTorrents = new Map();
    class LocalPeerClient extends WebTorrent {
        add(hash, options) {
            const torrent = super.add(hash, options);
            clientTorrents.set(hash, torrent);
            torrent.once('infoHash', () => torrent.addPeer('127.0.0.1:' + seeder.torrentPort));
            return torrent;
        }
    }
    const bridge = await startBridge({
        apiPort: 0,
        streamPort: 0,
        secret,
        client: new LocalPeerClient(localOptions),
        probeVideo: fixture ? undefined : async url => url.includes(seeded.infoHash) ? 0.01 : null
    });
    let closed = false;
    t.after(() => closed ? undefined : bridge.close());
    const check = magnet => fetch('http://127.0.0.1:' + bridge.port + '/check', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Torrent-Bridge': secret, Connection: 'close' },
        body: JSON.stringify({ magnet })
    });
    assert.equal((await check('invalid')).status, 400);
    const base = 'http://127.0.0.1:' + bridge.port;
    assert.equal((await fetch(base + '/limits', { headers: { 'X-Torrent-Bridge': 'jellyfin' } })).status, 404);
    assert.equal((await fetch(base + '/library/' + seeded.infoHash + '/0/test.mp4')).status, 404);
    const begin = performance.now();
    const selected = await fetch('http://127.0.0.1:' + bridge.port + '/select', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Torrent-Bridge': secret, Connection: 'close' },
        body: JSON.stringify({ magnets: ['magnet:?xt=urn:btih:' + 'b'.repeat(40), seeded.magnetURI] })
    });
    assert.equal(selected.status, 200);
    const selection = await selected.json();
    assert.match(selection.streamUrl, new RegExp(seeded.infoHash));
    assert.ok(performance.now() - begin < 12_000);
    const response = await check(seeded.magnetURI);
    assert.equal(response.status, 200);
    const result = await response.json();
    assert.equal(result.status, 'likelySmooth', JSON.stringify(result));
    assert.ok(result.videoMbps > 0);
    assert.equal(result.peers, 1);
    const ranged = await fetch(result.streamUrl, { headers: { Range: 'bytes=1024-2047' } });
    assert.equal(ranged.status, 206);
    assert.equal(ranged.headers.get('content-range'), 'bytes 1024-2047/' + video.length);
    assert.deepEqual(Buffer.from(await ranged.arrayBuffer()), video.subarray(1024, 2048));

    if (!fixture) {
        const torrent = clientTorrents.get(seeded.infoHash);
        const seekStart = video.length - 1024 * 1024;
        const seekPiece = Math.floor(seekStart / torrent.pieceLength);
        const middlePiece = Math.floor(video.length / 2 / torrent.pieceLength);
        assert.equal(torrent.bitfield.get(seekPiece), false);
        const savedUrl = result.libraryFiles[0].url;
        const stop = new AbortController();
        const opening = await fetch(savedUrl, { signal: stop.signal });
        assert.equal(opening.status, 200);
        assert.equal(torrent.bitfield.get(middlePiece), false);
        stop.abort();

        const seek = await fetch(savedUrl, { headers: { Range: `bytes=${seekStart}-` } });
        assert.equal(seek.status, 206);
        assert.equal(seek.headers.get('content-range'), `bytes ${seekStart}-${video.length - 1}/${video.length}`);
        assert.deepEqual(Buffer.from(await seek.arrayBuffer()), video.subarray(seekStart));
        assert.equal(torrent.bitfield.get(middlePiece), false);

        const rewind = await fetch(savedUrl, { headers: { Range: 'bytes=0-4095' } });
        assert.deepEqual(Buffer.from(await rewind.arrayBuffer()), video.subarray(0, 4096));
        const head = await fetch(savedUrl, { method: 'HEAD', headers: { Range: 'bytes=-4096' } });
        assert.equal(head.status, 206);
        assert.equal(head.headers.get('content-length'), '4096');
        const invalid = await fetch(savedUrl, { headers: { Range: `bytes=${video.length}-` } });
        assert.equal(invalid.status, 416);
        assert.equal(invalid.headers.get('content-range'), `bytes */${video.length}`);
    }

    assert.equal((await (await check(seeded.magnetURI)).json()).cached, true);
    const warning = await (await check(unknown.magnetURI)).json();
    assert.equal(warning.status, 'mayBuffer', JSON.stringify(warning));
    assert.equal(warning.videoMbps, null);
    assert.equal((await fetch(result.streamUrl)).status, 404);
    const savedUrl = result.libraryFiles[0].url;
    const tampered = new URL(savedUrl);
    tampered.pathname = tampered.pathname.replace('/0/', '/1/');
    assert.equal((await fetch(tampered)).status, 404);
    const originalPort = bridge.port;
    await bridge.close();
    closed = true;
    const restarted = await startBridge({
        apiPort: originalPort,
        streamPort: 0,
        secret,
        client: new LocalPeerClient(localOptions)
    });
    t.after(() => restarted.close());
    const restored = await fetch(savedUrl, { headers: { Range: 'bytes=8192-9215', Connection: 'close' } });
    assert.equal(restored.status, 206);
    assert.deepEqual(Buffer.from(await restored.arrayBuffer()), video.subarray(8192, 9216));
});
