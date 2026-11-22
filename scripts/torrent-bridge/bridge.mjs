import { execFile } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { createHmac, timingSafeEqual } from 'node:crypto';
import { freemem, totalmem } from 'node:os';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';
import { pipeline } from 'node:stream/promises';
import MemoryChunkStore from 'memory-chunk-store';
import WebTorrent from 'webtorrent';

const run = promisify(execFile);
const videoExtension = /\.(mp4|mkv|webm|mov|m4v|avi|ts|m2ts)$/i;
const MiB = 1024 * 1024;
const playbackWindowBytes = 4 * MiB;

async function* readPlaybackRange(torrent, file, start, end, signal) {
    const { pieceLength, store, bitfield } = torrent;
    const firstByte = file.offset + start;
    const lastByte = file.offset + end;
    const lastPiece = Math.floor(lastByte / pieceLength);
    const windowPieces = Math.max(1, Math.floor(playbackWindowBytes / pieceLength));
    const readPiece = promisify(store.get.bind(store));
    for (let first = Math.floor(firstByte / pieceLength); first <= lastPiece; first += windowPieces) {
        signal.throwIfAborted();
        const last = Math.min(lastPiece, first + windowPieces - 1);
        // WebTorrent stream selections are independent, even when two ranges overlap.
        torrent._select(first, last, 1, null, true);
        try {
            for (let piece = first; piece <= last; piece++) {
                signal.throwIfAborted();
                if (!bitfield.get(piece)) torrent.critical(piece, piece);
                while (!bitfield.get(piece)) {
                    await once(torrent, 'verified', { signal });
                }
                const offset = Math.max(0, firstByte - piece * pieceLength);
                const length = Math.min(pieceLength - offset, lastByte - piece * pieceLength - offset + 1);
                yield await readPiece(piece, { offset, length });
            }
        } finally {
            if (!torrent.destroyed) torrent._deselect(first, last, true);
        }
    }
}

async function serveVideo(request, response, torrent, file) {
    const headers = {
        'Accept-Ranges': 'bytes',
        'Cache-Control': 'no-store',
        'Content-Type': file.type,
        'X-Content-Type-Options': 'nosniff'
    };
    let start = 0;
    let end = file.length - 1;
    let status = 200;
    if (request.headers.range) {
        const range = /^bytes=(\d*)-(\d*)$/i.exec(request.headers.range);
        if (range && (range[1] || range[2])) {
            start = range[1] ? Number(range[1]) : Math.max(0, file.length - Number(range[2]));
            end = range[1] && range[2] ? Math.min(end, Number(range[2])) : end;
        }
        if (!range || (!range[1] && !range[2]) || start > end) {
            response.writeHead(416, { ...headers, 'Content-Range': `bytes */${file.length}` }).end();
            return;
        }
        status = 206;
        headers['Content-Range'] = `bytes ${start}-${end}/${file.length}`;
    }
    headers['Content-Length'] = end - start + 1;
    response.writeHead(status, headers);
    if (request.method === 'HEAD') {
        response.end();
        return;
    }
    response.flushHeaders();
    const stop = new AbortController();
    const cancel = () => stop.abort();
    response.once('close', cancel);
    torrent.once('close', cancel);
    response.setTimeout(30_000, cancel);
    try {
        await pipeline(readPlaybackRange(torrent, file, start, end, stop.signal), response, { signal: stop.signal });
    } catch {
        response.destroy();
    } finally {
        response.off('close', cancel);
        response.off('timeout', cancel);
        torrent.off('close', cancel);
    }
}

function streamPath(torrent, file) {
    return `/webtorrent/${torrent.infoHash}/${file.path.split(/[/\\]/).map(encodeURIComponent).join('/')}`;
}

function infoHash(magnet) {
    if (typeof magnet !== 'string' || !magnet.startsWith('magnet:?')) return null;
    const topic = new URLSearchParams(magnet.slice(8)).get('xt');
    return /^urn:btih:([a-f0-9]{40}|[a-z2-7]{32})$/i.exec(topic)?.[1].toLowerCase();
}

async function sampleOpening(file, signal = AbortSignal.timeout(10_000), sampleBytes = 4 * MiB) {
    signal.throwIfAborted();
    const target = Math.min(file.length, sampleBytes);
    const start = performance.now();
    let bytes = 0;
    const stream = file.createReadStream({ start: 0, end: target - 1 });
    const stop = () => stream.destroy(signal.reason);
    signal.addEventListener('abort', stop, { once: true });
    try {
        for await (const chunk of stream) bytes += chunk.length;
    } catch (error) {
        if (!signal.aborted) throw error;
    } finally {
        signal.removeEventListener('abort', stop);
        stream.destroy();
    }
    return { bytes, complete: bytes === target, mbps: bytes * 8 / Math.max(1, performance.now() - start) / 1000 };
}

async function probe(url, length) {
    const { stdout } = await run(process.env.TORRENT_FFPROBE || 'ffprobe', [
        '-v', 'error', '-protocol_whitelist', 'http,tcp',
        '-format_whitelist', 'mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,mpegts', '-show_entries',
        'format=duration:stream=codec_type', '-of', 'json', url
    ], { timeout: 6_000, windowsHide: true });
    const data = JSON.parse(stdout);
    const duration = Number(data.format?.duration);
    if (!data.streams?.some(stream => stream.codec_type === 'video') || !(duration > 0 && Number.isFinite(duration))) {
        throw new Error('Video duration could not be measured.');
    }
    return length * 8 / duration / 1_000_000;
}

export async function startBridge({
    apiPort = 18781, streamPort = 18782, maxBytes = 512 * MiB,
    client = new WebTorrent({
        lsd: false, tracker: { announce: ['udp://tracker.opentrackr.org:1337/announce', 'udp://open.demonii.com:1337/announce'] },
        webSeeds: false, utPex: false, natUpnp: false, natPmp: false
    }),
    probeVideo = probe, secret = process.env.TORRENT_BRIDGE_SECRET
} = {}) {
    if (typeof secret !== 'string' || Buffer.byteLength(secret) < 32) {
        client.destroy();
        throw new Error('TORRENT_BRIDGE_SECRET must contain at least 32 bytes.');
    }

    function matchesSecret(actual, expected) {
        if (typeof actual !== 'string') return false;
        const supplied = Buffer.from(actual);
        const wanted = Buffer.from(expected);
        return supplied.length === wanted.length && timingSafeEqual(supplied, wanted);
    }

    function signature(path) {
        return createHmac('sha256', secret).update(path).digest('base64url');
    }

    function signedPath(path) {
        return path + '?ticket=' + signature(path);
    }

    function authorizedPath(url = '') {
        const marker = url.lastIndexOf('?ticket=');
        if (marker < 0) return null;
        const path = url.slice(0, marker);
        return matchesSecret(url.slice(marker + 8), signature(path)) ? path : null;
    }

    client.on('error', error => console.error(error.message));
    const streaming = createServer(async (request, response) => {
        if (request.headers.host !== `127.0.0.1:${streaming.address().port}`) {
            response.writeHead(403).end();
            return;
        }
        const path = authorizedPath(request.url);
        const selected = torrent;
        const file = selected?.ready && !selected.destroyed && selected.files.find(item =>
            item.length > 0 && videoExtension.test(item.name) && streamPath(selected, item) === path);
        if (!file || !['GET', 'HEAD'].includes(request.method)) {
            response.writeHead(404).end();
            return;
        }
        activeReaders++;
        response.once('close', () => activeReaders--);
        await serveVideo(request, response, selected, file);
    });
    streaming.listen(streamPort, '127.0.0.1');
    try {
        await once(streaming, 'listening');
    } catch (error) {
        client.destroy();
        throw error;
    }
    const streamOrigin = 'http://127.0.0.1:' + streaming.address().port;
    let torrent;
    let lastResult;
    let currentHash;
    let busy = false;
    let loadPromise;
    let activeReaders = 0;

    function addCandidate(hash) {
        const candidate = client.add(hash, { store: MemoryChunkStore, deselect: true, destroyStoreOnDestroy: true });
        candidate.on('error', () => {});
        return candidate;
    }

    function releaseTorrent(candidate) {
        return candidate && !candidate.destroyed ? new Promise(done => candidate.destroy(done)) : Promise.resolve();
    }

    function videoFor(candidate) {
        return candidate.files.filter(file => file.length > 0 && videoExtension.test(file.name)
            && !/(^|[. _-])sample([. _-]|$)/i.test(file.name))
            .sort((a, b) => b.length - a.length)[0];
    }

    async function ready(candidate, signal) {
        if (!candidate.ready) await once(candidate, 'ready', { signal });
        if (candidate.length > maxBytes || candidate.pieceLength > 16 * MiB) {
            throw Object.assign(new Error('This torrent exceeds the bridge’s RAM limit.'), { code: 'TORRENT_RAM_LIMIT' });
        }
    }

    async function loadTorrent(hash) {
        await releaseTorrent(torrent);
        lastResult = null;
        currentHash = hash;
        torrent = addCandidate(hash);
        await ready(torrent, AbortSignal.timeout(15_000));
        return torrent;
    }

    async function getTorrent(hash) {
        if (hash === currentHash && loadPromise) return loadPromise;
        if (hash === currentHash && torrent?.ready && !torrent.destroyed) return torrent;
        currentHash = hash;
        loadPromise = loadTorrent(hash);
        try { return await loadPromise; }
        catch (error) {
            if (torrent && !torrent.destroyed) await new Promise(done => torrent.destroy(done));
            throw error;
        } finally { loadPromise = undefined; }
    }

    async function assess(hash) {
        if (hash === currentHash && lastResult?.streamUrl && torrent && !torrent.destroyed) {
            return { ...lastResult, cached: true };
        }
        const activeTorrent = await getTorrent(hash);
        const file = videoFor(activeTorrent);
        if (!file) return { status: 'unavailable', message: 'No supported video file was found.' };
        const sample = await sampleOpening(file);
        if (!sample.bytes) {
            return { status: 'unavailable', message: 'No opening video data arrived within 10 seconds.' };
        }
        return assessSample(hash, activeTorrent, file, sample);
    }

    async function assessSample(hash, activeTorrent, file, sample) {
        const streamUrl = new URL(signedPath(streamPath(activeTorrent, file)), streamOrigin).href;
        const videoMbps = await probeVideo(streamUrl, file.length).catch(() => null);
        const likelySmooth = sample.complete && videoMbps !== null && sample.mbps >= videoMbps * 1.5;
        return {
            status: likelySmooth ? 'likelySmooth' : 'mayBuffer',
            message: likelySmooth ? 'The opening sample arrived faster than the average video bitrate.'
                : videoMbps === null ? 'The video bitrate could not be verified.'
                    : 'The opening sample did not establish enough download headroom.',
            fileName: file.name,
            sampleMbps: sample.mbps,
            videoMbps,
            peers: activeTorrent.numPeers,
            streamUrl,
            libraryFiles: activeTorrent.files.flatMap((item, index) => item.length && videoExtension.test(item.name)
                ? [{ index, name:item.name, url: `http://127.0.0.1:${api.address().port}${signedPath(`/library/${hash}/${index}/${encodeURIComponent(item.name)}`)}` }] : []),
            selectedFile: activeTorrent.files.indexOf(file)
        };
    }

    async function selectRelease(hashes) {
        if (hashes.includes(currentHash) && lastResult?.streamUrl && torrent && !torrent.destroyed) return lastResult;
        if (activeReaders > 0) {
            return null;
        }
        if (loadPromise) return null;
        const previous = torrent;
        torrent = undefined;
        currentHash = undefined;
        lastResult = null;
        await releaseTorrent(previous);

        const stop = new AbortController();
        const signal = AbortSignal.any([stop.signal, AbortSignal.timeout(17_000)]);
        const candidates = hashes.map(hash => ({ hash, torrent: addCandidate(hash) }));
        let winner;
        try {
            winner = await Promise.any(candidates.map(async entry => {
                await ready(entry.torrent, signal);
                const file = videoFor(entry.torrent);
                if (!file) throw new Error('No video file.');
                const sample = await sampleOpening(file, signal, 2 * MiB);
                if (!sample.complete) throw new Error('Opening buffer did not fill.');
                return { ...entry, file, sample };
            }));
        } catch {
            return { status:'unavailable', message:'None of the tested swarms supplied enough video data. Try another title or release.' };
        } finally {
            stop.abort();
            await Promise.all(candidates.filter(entry => entry.torrent !== winner?.torrent).map(entry => releaseTorrent(entry.torrent)));
        }

        currentHash = winner.hash;
        torrent = winner.torrent;
        lastResult = await assessSample(winner.hash, torrent, winner.file, winner.sample);
        return lastResult;
    }

    async function serveLibrary(request, response, hash, index, filename) {
        if (currentHash !== hash && (busy || activeReaders > 0)) {
            response.writeHead(503, { 'Retry-After': '5' }).end();
            return;
        }
        activeReaders++;
        response.once('close', () => activeReaders--);
        try {
            const selected = await getTorrent(hash);
            const file = selected.files[index];
            if (!file?.length || encodeURIComponent(file.name) !== filename || !videoExtension.test(file.name)) {
                response.writeHead(404).end();
                return;
            }
            if (response.destroyed) return;
            await serveVideo(request, response, selected, file);
        } catch {
            if (!response.destroyed) response.writeHead(503, { 'Retry-After': '5' }).end();
        }
    }

    const api = createServer(async (request, response) => {
        const send = (status, body) => {
            response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
            response.end(JSON.stringify(body));
        };
        if (request.headers.host !== `127.0.0.1:${api.address().port}`) return send(403, { error: 'Invalid host.' });
        const canManage = matchesSecret(request.headers['x-torrent-bridge'], secret);
        const library = /^\/library\/([a-f0-9]{40}|[a-z2-7]{32})\/(\d{1,6})\/([^/?]+)$/.exec(authorizedPath(request.url) ?? '');
        if (library && (request.method === 'GET' || request.method === 'HEAD')) {
            return serveLibrary(request, response, library[1], Number(library[2]), library[3]);
        }
        if (request.method === 'GET' && request.url === '/limits' && canManage) {
            return send(200, { maxBytes });
        }
        if (request.method !== 'POST' || !['/check', '/select'].includes(request.url) || !canManage) {
            return send(404, { error: 'Unknown endpoint.' });
        }
        if (busy) return send(409, { error: 'A torrent check is already running.' });
        busy = true;
        try {
            let body = '';
            request.setEncoding('utf8');
            for await (const chunk of request) {
                body += chunk;
                if (body.length > 8192) {
                    send(413, { error: 'Magnet link is too long.' });
                    return;
                }
            }
            let input;
            try { input = JSON.parse(body); } catch { return send(400, { error:'Invalid playback request.' }); }
            if (request.url === '/select') {
                const hashes = Array.isArray(input?.magnets) ? [...new Set(input.magnets.map(infoHash).filter(Boolean))].slice(0, 4) : [];
                if (!hashes.length) return send(400, { error:'No compatible BitTorrent v1 magnet links were supplied.' });
                const selected = await selectRelease(hashes);
                return selected ? send(200, selected) : send(409, { error:'Another torrent is playing. Try again when it finishes.' });
            }
            const hash = infoHash(input?.magnet);
            if (!hash) return send(400, { error: 'Paste a BitTorrent v1 magnet link.' });
            if (hash !== currentHash && activeReaders > 0) {
                return send(409, { error: 'Another torrent is playing. Try again when it finishes.' });
            }
            try {
                lastResult = await assess(hash);
            } catch (error) {
                lastResult = { status: 'unavailable', message: error.code === 'TORRENT_RAM_LIMIT' ? error.message
                    : 'The torrent metadata or video could not be read in time.' };
            }
            if (!lastResult.streamUrl && activeReaders === 0 && torrent && !torrent.destroyed) {
                await new Promise(done => torrent.destroy(done));
            }
            send(200, lastResult);
        } catch {
            if (!response.destroyed && !response.headersSent) send(500, { error: 'The bridge could not complete the check.' });
        } finally {
            busy = false;
        }
    });
    api.requestTimeout = 15_000;
    api.listen(apiPort, '127.0.0.1');
    try {
        await once(api, 'listening');
    } catch (error) {
        streaming.close();
        client.destroy();
        throw error;
    }
    return {
        port: api.address().port,
        close: async () => {
            const closed = new Promise(done => api.close(done));
            api.closeAllConnections();
            streaming.close();
            streaming.closeAllConnections();
            await Promise.all([closed, new Promise(done => client.destroy(done))]);
        }
    };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
    const jellyfinPid = Number(process.env.TORRENT_JELLYFIN_PID);
    if (Number.isSafeInteger(jellyfinPid) && jellyfinPid > 0) {
        const watchParent = setInterval(() => {
            try {
                process.kill(jellyfinPid, 0);
            } catch (error) {
                if (error.code === 'ESRCH') process.exit(0);
            }
        }, 2_000);
        watchParent.unref();
    }
    const maxBytes = Math.min(4096 * MiB, Math.floor(totalmem() / 8), Math.floor(freemem() / 3));
    const port = Number(process.env.TORRENT_BRIDGE_PORT);
    const bridge = await startBridge({ maxBytes, apiPort: Number.isInteger(port) && port > 0 && port <= 65535 ? port : 18781 });
    console.log('Torrent playback check listening on 127.0.0.1:' + bridge.port);
}
