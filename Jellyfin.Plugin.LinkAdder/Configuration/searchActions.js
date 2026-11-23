const jobs = new Map();
const value = (item, name) => item?.[name[0].toLowerCase() + name.slice(1)] ?? item?.[name];

function pathKey(path) {
    const normalized = String(path ?? '').replace(/\\/g, '/').replace(/\/+$/, '');
    return /^[a-z]:|^\/\//i.test(normalized) ? normalized.toLowerCase() : normalized;
}

export async function searchLibrary(query, filter, libraries) {
    const userId = ApiClient.getCurrentUserId();
    const animeLibraries = [...new Set(libraries.filter(library => value(library, 'MediaType') === 'anime')
        .map(library => value(library, 'Id')).filter(Boolean))];
    const options = {
        SearchTerm: query, Recursive: true, Limit: filter ? 100 : 30,
        ImageTypeLimit: 1, EnableImageTypes: 'Primary'
    };
    const normal = filter === 'anime' ? Promise.resolve({ Items: [] }) : ApiClient.getItems(userId, {
        ...options, IncludeItemTypes: filter === 'movie' ? 'Movie' : filter === 'series' ? 'Series' : 'Movie,Series'
    });
    const [result, ...animeResults] = await Promise.all([
        normal,
        ...animeLibraries.map(ParentId => ApiClient.getItems(userId, {
            ...options, ParentId, IncludeItemTypes: 'Movie,Series'
        }).catch(() => ({ Items: [] })))
    ]);
    const anime = new Map(animeResults.flatMap(group => group.Items || []).map(item => [item.Id, item]));
    const local = new Map((result.Items || []).map(item => [item.Id, item]));
    if (!filter || filter === 'anime') for (const [id, item] of anime) local.set(id, item);
    return [...local.values()].map(item => ({
        ...item, category: anime.has(item.Id) ? 'anime' : item.Type === 'Movie' ? 'movie' : 'series'
    })).filter(item => !filter || item.category === filter);
}

function titleKey(name) {
    const title = String(name ?? '').normalize('NFKC')
        .replace(/[._]+/g, ' ')
        .replace(/\b(?:2160p|1080p|720p|480p|web[- ]?dl|webrip|bluray|x26[45]|hevc)\b.*$/i, '')
        .trim();
    const year = title.match(/\s*[(\[]((?:19|20)\d{2})[)\]]\s*$/);
    return {
        name: (year ? title.slice(0, year.index) : title).toLowerCase().replace(/[^\p{L}\p{M}\p{N}]+/gu, ' ').trim(),
        year: year ? Number(year[1]) : null
    };
}

function existingItem(item, mediaType, localItems) {
    const wanted = titleKey(item.name);
    if (!wanted.name) return null;
    const matches = localItems.filter(entry => {
        if (entry.category !== mediaType || !['Movie', 'Series'].includes(entry.Type)) return false;
        const title = titleKey(entry.Name);
        return title.name === wanted.name && (!wanted.year || (entry.ProductionYear || title.year) === wanted.year);
    });
    return matches.length === 1 ? matches[0] : null;
}

const states = {
    add: ['add', 'FTP'],
    adding: ['downloading', 'Adding…'],
    indexing: ['downloading', 'Indexing…'],
    pending: ['refresh', 'Check again'],
    failed: ['add', 'Retry Add'],
    ready: ['play_arrow', 'Play']
};

function paint(button, state) {
    const [icon, label] = states[state.phase];
    const busy = state.phase === 'adding' || state.phase === 'indexing';
    button.dataset.linkAdderPhase = state.phase;
    button.disabled = busy;
    button.title = state.message || label;
    button.setAttribute('aria-busy', String(busy));
    const symbol = document.createElement('span');
    symbol.className = 'material-icons linkAdderActionIcon';
    symbol.setAttribute('aria-hidden', 'true');
    symbol.textContent = icon;
    const text = document.createElement('span');
    text.setAttribute('aria-live', 'polite');
    text.textContent = label;
    button.replaceChildren(symbol, text);
    if (button.nextElementSibling?.matches('.linkAdderActionStatus')) button.nextElementSibling.remove();
    if (state.message) {
        const status = document.createElement('small');
        status.className = 'linkAdderActionStatus';
        status.setAttribute('role', 'status');
        status.textContent = state.message;
        button.after(status);
    }
}

function transition(job, phase, message = '') {
    Object.assign(job, {phase, message});
    for (const button of job.buttons) {
        if (button.isConnected) paint(button, job);
        else job.buttons.delete(button);
    }
    if (phase === 'ready' || phase === 'pending' || phase === 'failed') job.buttons.clear();
}

export async function waitForIndexedItem(mediaType, added) {
    const folder = pathKey(value(added, 'TitlePath'));
    const files = new Set([...(value(added, 'Created') || []), ...(value(added, 'Skipped') || [])].map(pathKey));
    const title = value(added, 'Title');
    const libraryId = value(added, 'LibraryId');
    const query = title?.replace(/\s*\((?:19|20)\d{2}\).*$/, '') || title;
    const pathMatches = (actual, expected) => {
        if (!actual || !expected) return false;
        if (!libraryId) return actual === expected;
        const key = /^[a-z]:|^\/\//i.test(actual) ? expected.toLowerCase() : expected;
        return actual.endsWith('/' + key);
    };
    const matches = entry => {
        const path = pathKey(entry.Path);
        return mediaType === 'movie' ? [...files].some(file => pathMatches(path, file)) : pathMatches(path, folder);
    };
    const scope = libraryId ? { ParentId: libraryId } : {};
    const userId = ApiClient.getCurrentUserId();
    const itemType = mediaType === 'movie' ? 'Movie' : 'Series';
    const deadline = Date.now() + 90000;
    let attempts = 0;
    while (Date.now() < deadline) {
        const result = await ApiClient.getItems(userId, {
            SearchTerm:query, IncludeItemTypes:itemType, Recursive:true, ...scope, Limit:50, Fields:'Path'
        });
        const match = (result.Items || []).find(matches);
        if (match) return match;
        if (++attempts % 3 === 0) {
            const recent = await ApiClient.getItems(userId, {
                IncludeItemTypes:itemType, Recursive:true, ...scope, SortBy:'DateCreated', SortOrder:'Descending', Limit:75, Fields:'Path'
            });
            const exactPath = (recent.Items || []).find(matches);
            if (exactPath) return exactPath;
        }
        await new Promise(resolve => setTimeout(resolve, 2500));
    }
    return null;
}

export function openItem(item) {
    const destination = 'details?id=' + encodeURIComponent(item.Id);
    if (window.Dashboard?.navigate) window.Dashboard.navigate(destination);
    else window.location.hash = '#/' + destination;
}

export async function playItem(item){
    const player=window.playbackManager||window.PlaybackManager;
    if(player?.play){
        try{await player.play({items:[item]});return}catch(_){}
    }
    openItem(item);
    const route='details?id='+encodeURIComponent(item.Id);
    for(let attempt=0;attempt<75;attempt++){
        if(window.location.hash.includes(route)){
            const button=[...document.querySelectorAll('#itemDetailPage button[data-action="play"],#itemDetailPage button[data-action="resume"]')]
                .find(element=>!element.disabled && element.getClientRects().length);
            if(button){button.click();return}
        }
        await new Promise(resolve=>setTimeout(resolve,200));
    }
}


function ensureStyles() {
    if (document.getElementById('linkAdderActionStyles')) return;
    const style = document.createElement('style');
    style.id = 'linkAdderActionStyles';
    style.textContent = [
        '.linkAdderMediaAction .linkAdderActionIcon {font-size:1.3em;margin-inline-end:.35em;vertical-align:middle}',
        '.linkAdderMediaAction[aria-busy=true] .linkAdderActionIcon {animation:linkAdderDownload .8s ease-in-out infinite alternate}',
        '.linkAdderActionStatus {display:block;margin-top:.35em;overflow-wrap:anywhere}',
        '@keyframes linkAdderDownload {from{transform:translateY(-.2em);opacity:.6}to{transform:translateY(.2em);opacity:1}}',
        '@media(prefers-reduced-motion:reduce){.linkAdderMediaAction[aria-busy=true] .linkAdderActionIcon{animation:none}}'
    ].join('\n');
    document.head.appendChild(style);
}

export function bind(button, item, localItems, call) {
    ensureStyles();
    button.classList.add('linkAdderMediaAction');
    const mediaType = item.mediaType || item.type;
    const key = JSON.stringify([ApiClient.getCurrentUserId(), mediaType, item.url]);
    const existing = existingItem(item, mediaType, localItems || []);
    const initial = jobs.get(key);
    if (!existing && (initial?.phase === 'adding' || initial?.phase === 'indexing')) initial.buttons.add(button);
    paint(button, existing ? {phase:'ready'} : initial || {phase:'add'});

    button.onclick = async () => {
        let job = jobs.get(key);
        if (existing) return playItem(existing);
        if (!job) {
            job = {phase:'add', buttons:new Set()};
            jobs.set(key, job);
        }
        if (job.phase === 'ready') return playItem(job.item);
        job.buttons.add(button);
        if (job.phase === 'adding' || job.phase === 'indexing') return;
        if (!job.added) {
            transition(job, 'adding');
            try {
                job.added = await call('Add', 'POST', {url:item.url, mediaType, title:item.name});
            } catch (error) {
                transition(job, 'failed', error.message || 'The title could not be added.');
                return;
            }
        }
        transition(job, 'indexing');
        job.item = await waitForIndexedItem(mediaType, job.added).catch(() => null);
        transition(job, job.item ? 'ready' : 'pending', job.item ? '' : 'STRM files are ready. Jellyfin is still indexing this title.');
    };
}

export function bindPoster(poster, button, title) {
    poster.onclick = () => {
        if (button.disabled) return;
        const phase = button.dataset.linkAdderPhase;
        if (phase === 'ready' || phase === 'pending' || window.confirm('Add “' + title + '” to Jellyfin?')) {
            button.click();
        }
    };
}
