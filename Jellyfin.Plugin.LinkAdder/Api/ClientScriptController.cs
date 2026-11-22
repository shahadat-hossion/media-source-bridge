using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LinkAdder.Api;

/// <summary>
/// Serves the web-client enhancement used for the unified Search &amp; Add button.
/// </summary>
[ApiController]
[Route("LocalIspFtpLoader")]
public sealed class ClientScriptController : ControllerBase
{
    /// <summary>
    /// Gets the browser script that adds the header button.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("client.js")]
    [Produces("application/javascript")]
    public ContentResult GetClientScript()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return Content(ClientScript, "application/javascript", System.Text.Encoding.UTF8);
    }

    [AllowAnonymous]
    [HttpGet("cinema-hero.jpg")]
    public FileStreamResult GetCinemaHero()
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        var image = typeof(ClientScriptController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LinkAdder.Configuration.cinema-hero.jpg");
        return File(image!, "image/jpeg");
    }

    [AllowAnonymous]
    [HttpGet("search-actions.js")]
    public FileStreamResult GetSearchActions()
    {
        Response.Headers.CacheControl = "no-cache";
        var script = typeof(ClientScriptController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LinkAdder.Configuration.searchActions.js");
        return File(script!, "application/javascript");
    }

    [AllowAnonymous]
    [HttpGet("torrent-search.js")]
    public FileStreamResult GetTorrentSearch()
    {
        Response.Headers.CacheControl = "no-cache";
        var script = typeof(ClientScriptController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LinkAdder.Configuration.torrentSearch.js");
        return File(script!, "application/javascript");
    }

    private const string ClientScript = """
        (() => {
            'use strict';

            document.getElementById('localIspFtpLoaderDiscoverWidget')?.remove();
            document.getElementById('localIspFtpLoaderDiscoverWidgetStyles')?.remove();

            if (window.__localIspFtpLoaderHeaderButton) return;
            window.__localIspFtpLoaderHeaderButton = true;

            const buttonId = 'localIspFtpLoaderHeaderButton';
            const containerId = 'localIspFtpLoaderHeaderButtonContainer';
            const userMenuItemId = 'localIspFtpLoaderUserMenuItem';
            const navigationStyleId = 'localIspFtpLoaderNavigationStyles';
            const sidebarMenuId = 'localIspFtpLoaderSidebarPages';
            const landingRoute = '#/configurationpage?name=localispplugins';
            const addMediaRoute = '#/search';
            const importLinkRoute = '#/configurationpage?name=localispimportlink';
            const discoverRoute = '#/configurationpage?name=localispdiscovery';
            const sidebarPages = [
                ['Discover', discoverRoute],
                ['Search', '#/configurationpage?name=localispsearch'],
                ['Import Link', importLinkRoute],
                ['Setup Media', '#/configurationpage?name=localispservers']
            ];
            const sidebarParents = new WeakSet();
            let replaceDefaultSearch = null;

            function isVisible(element) {
                return Boolean(element && element.isConnected && element.getClientRects().length);
            }

            function openAddMedia() {
                if (window.location.hash.startsWith(addMediaRoute)) {
                    document.querySelector('#searchTextInput')?.focus();
                    return;
                }
                window.location.hash = addMediaRoute;
            }

            function openImportLink() {
                window.location.hash = importLinkRoute;
            }

            function ensureSidebarMenu() {
                const parent = document.querySelector(`.MuiDrawer-paper a[href$="${landingRoute}"], .mainDrawer a[href$="${landingRoute}"]`);
                const row = parent?.closest('li') ?? parent;
                const existing = document.getElementById(sidebarMenuId);
                if (!row) {
                    existing?.remove();
                    return;
                }

                if (!document.getElementById(navigationStyleId)) {
                    const style = document.createElement('style');
                    style.id = navigationStyleId;
                    style.textContent = `
                        #${sidebarMenuId} { list-style: none; }
                        a[aria-controls="${sidebarMenuId}"]::after { content: '▸'; margin-inline-start: auto; }
                        a[aria-controls="${sidebarMenuId}"][aria-expanded="true"]::after { content: '▾'; }
                        #${sidebarMenuId} a:not([hidden]) { display: block; color: inherit; text-decoration: none; padding: .55rem .75rem .55rem 3.5rem; }
                        #${sidebarMenuId} a:hover, #${sidebarMenuId} a[aria-current="page"] { background: rgba(128,128,128,.2); }
                    `;
                    document.head.appendChild(style);
                }

                let menu = existing;
                if (row.nextElementSibling !== menu || !menu) {
                    menu?.remove();
                    menu = document.createElement(row.parentElement.matches('ul, ol') ? 'li' : 'div');
                    menu.id = sidebarMenuId;
                    for (const [label, route] of sidebarPages) {
                        const link = document.createElement('a');
                        link.textContent = label;
                        link.href = route;
                        if (label === 'Setup Media') {
                            link.hidden = true;
                            pluginCall('CanManage').then(() => { link.hidden = false; }).catch(() => {});
                        }
                        menu.appendChild(link);
                    }
                    row.after(menu);
                }

                if (!sidebarParents.has(parent)) {
                    sidebarParents.add(parent);
                    parent.setAttribute('aria-controls', sidebarMenuId);
                    parent.addEventListener('click', (event) => {
                        if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
                        const submenu = document.getElementById(sidebarMenuId);
                        if (!submenu) return;
                        const expanding = submenu.hidden;
                        setSidebarExpanded(parent, submenu, expanding);
                        if (!expanding || window.location.hash === landingRoute) event.preventDefault();
                    });
                }

                const hash = window.location.hash;
                if (menu.dataset.route === hash) return;
                menu.dataset.route = hash;
                const links = Array.from(menu.querySelectorAll('a'));
                setSidebarExpanded(parent, menu, hash === landingRoute || links.some(link => link.hash === hash));
                for (const link of links) {
                    if (link.hash === hash) link.setAttribute('aria-current', 'page');
                    else link.removeAttribute('aria-current');
                }
            }

            function setSidebarExpanded(parent, menu, expanded) {
                menu.hidden = !expanded;
                parent.setAttribute('aria-expanded', String(expanded));
            }

            function ensureUserMenuItem() {
                const menu = document.getElementById('app-user-menu');
                if (!menu || !isVisible(menu) || menu.querySelector(`#${userMenuItemId}`)) return;

                const enhancedLabel = Array.from(menu.querySelectorAll('*')).find((element) =>
                    element.children.length === 0 && element.textContent?.trim() === 'Jellyfin Enhanced');
                const enhancedItem = enhancedLabel?.closest('[role="menuitem"], a, button, li');
                if (!enhancedItem) return;

                const item = enhancedItem.cloneNode(true);
                item.id = userMenuItemId;
                item.removeAttribute('href');
                item.setAttribute('role', 'menuitem');
                item.setAttribute('tabindex', '0');

                const label = Array.from(item.querySelectorAll('*')).find((element) =>
                    element.children.length === 0 && element.textContent?.trim() === 'Jellyfin Enhanced');
                if (label) label.textContent = 'Media Source Bridge';

                const icon = item.querySelector('svg, .material-icons');
                if (icon) {
                    const storageIcon = document.createElement('span');
                    storageIcon.className = 'material-icons storage';
                    storageIcon.setAttribute('aria-hidden', 'true');
                    storageIcon.textContent = 'storage';
                    icon.replaceWith(storageIcon);
                }

                const activate = (event) => {
                    if (event.type === 'keydown' && event.key !== 'Enter' && event.key !== ' ') return;
                    event.preventDefault();
                    event.stopPropagation();
                    document.querySelector('[aria-controls="app-user-menu"]')?.click();
                    openImportLink();
                };
                item.addEventListener('click', activate);
                item.addEventListener('keydown', activate);
                enhancedItem.insertAdjacentElement('afterend', item);
            }

            function createButton() {
                const button = document.createElement('button');
                button.id = buttonId;
                button.type = 'button';
                button.className = 'headerButton headerButtonRight paper-icon-button-light headerAddMediaButton';
                button.setAttribute('is', 'paper-icon-button-light');
                button.title = 'Search & Add Media';
                button.setAttribute('aria-label', 'Search & Add Media');
                button.innerHTML = '<span class="material-icons search" aria-hidden="true"></span>';
                button.addEventListener('click', openAddMedia);
                return button;
            }

            function findVisibleSearchButton() {
                const candidates = Array.from(document.querySelectorAll(
                    '.headerSearchButton, .MuiToolbar-root button[aria-label], .MuiToolbar-root button[title]'))
                    .filter((candidate) => candidate.id !== buttonId);
                return candidates.find((candidate) => {
                    if (!isVisible(candidate)) return false;
                    if (candidate.classList.contains('headerSearchButton')) return true;
                    const label = `${candidate.getAttribute('aria-label') || ''} ${candidate.title || ''}`;
                    return /search/i.test(label) || Boolean(candidate.querySelector('.material-icons.search'));
                }) || null;
            }

            function findHeaderTray() {
                const enhancedTray = window.JellyfinEnhanced?.helpers?.getHeaderButtonTray?.();
                if (enhancedTray && isVisible(enhancedTray)) return enhancedTray;

                const visibleLegacy = Array.from(document.querySelectorAll('.headerRight')).find(isVisible);
                if (visibleLegacy) return visibleLegacy;

                const searchButton = findVisibleSearchButton();
                if (searchButton?.parentElement) return searchButton.parentElement;

                const userMenuButton = document.querySelector('[aria-controls="app-user-menu"]');
                const toolbar = userMenuButton?.closest('.MuiToolbar-root')
                    || document.querySelector('.MuiAppBar-root .MuiToolbar-root');
                if (!toolbar) return null;

                let userMenuBox = userMenuButton;
                while (userMenuBox && userMenuBox.parentElement !== toolbar) {
                    userMenuBox = userMenuBox.parentElement;
                }

                const preceding = userMenuBox?.previousElementSibling;
                if (preceding && !preceding.matches('button, a, [role="button"]')) return preceding;
                return null;
            }

            function ensureButton() {
                document.getElementById('localIspFtpLoaderDiscoverWidget')?.remove();
                document.getElementById('localIspFtpLoaderDiscoverWidgetStyles')?.remove();
                const headerTray = findHeaderTray();
                if (!headerTray) return;

                const existingContainer = document.getElementById(containerId);
                if (existingContainer?.parentElement === headerTray && isVisible(existingContainer)) return;
                existingContainer?.remove();

                const container = document.createElement('div');
                container.id = containerId;
                container.appendChild(createButton());

                const searchButton = findVisibleSearchButton();
                if (searchButton?.parentElement === headerTray) {
                    headerTray.insertBefore(container, searchButton);
                } else {
                    headerTray.prepend(container);
                }
            }

            function applySearchReplacement() {
                const candidates = Array.from(document.querySelectorAll(
                    '.headerSearchButton, .MuiToolbar-root [aria-label], .MuiToolbar-root [title], [data-local-isp-hidden="true"]'))
                    .filter((candidate) => {
                        if (candidate.id === buttonId) return false;
                        const label = `${candidate.getAttribute('aria-label') || ''} ${candidate.title || ''}`;
                        return candidate.classList.contains('headerSearchButton')
                            || /search/i.test(label)
                            || candidate.dataset.localIspHidden === 'true';
                    });
                for (const nativeSearch of candidates) {
                    if (replaceDefaultSearch === true) {
                        nativeSearch.dataset.localIspHidden = 'true';
                        nativeSearch.style.setProperty('display', 'none', 'important');
                    } else if (nativeSearch.dataset.localIspHidden === 'true') {
                        nativeSearch.style.removeProperty('display');
                        delete nativeSearch.dataset.localIspHidden;
                    }
                }
            }

            async function loadSettings() {
                try {
                    const result = await ApiClient.ajax({
                        url: ApiClient.getUrl('Plugins/LinkAdder/SearchUiSettings'),
                        type: 'GET',
                        dataType: 'json'
                    });
                    replaceDefaultSearch = result.replaceDefaultSearch ?? result.ReplaceDefaultSearch ?? true;
                } catch (_) {
                    replaceDefaultSearch = false;
                }
                ensureEnhancements();
            }

            const remoteSectionId = 'localIspNativeSearchResults';
            const remoteStyleId = 'localIspNativeSearchStyles';
            let nativeSearchInput = null;
            let nativeSearchTimer = 0;
            let nativeSearchSequence = 0;
            let nativeLocalItems = [];
            let nativeRemoteItems = [];
            let nativeRemoteStatus = '';
            let nativeTorrentGroups = [];
            let nativeTorrentStatus = '';
            let searchActions;
            let torrentSearchModule;
            let torrentActions;
            let canManageTorrents;
            let nativeMediaType = '';
            let nativeSource = '';

            const value = (item, name) => item?.[name[0].toLowerCase() + name.slice(1)] ?? item?.[name];
            const escapeHtml = (text) => String(text ?? '').replace(/[&<>"']/g, (character) => ({
                '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
            })[character]);

            async function pluginCall(path, method = 'GET', body) {
                try {
                    return await ApiClient.ajax({
                        url: ApiClient.getUrl('Plugins/LinkAdder/' + path),
                        type: method,
                        dataType: 'json',
                        contentType: 'application/json',
                        data: body ? JSON.stringify(body) : undefined
                    });
                } catch (error) {
                    let response;
                    try { response = JSON.parse(await error.text?.() ?? error.responseText ?? '{}'); } catch (_) {}
                    throw new Error(response?.error || response?.detail || error.message || 'Request failed.');
                }
            }

            function ensureNativeSearchStyles() {
                if (document.getElementById(remoteStyleId)) return;
                const style = document.createElement('style');
                style.id = remoteStyleId;
                style.textContent = `
                    #${remoteSectionId} { position:fixed;z-index:20;inset:4.2em 0 0;overflow:auto;background:#101010;padding:1.2em 0 5em; }
                    #${remoteSectionId} .localIspNativeFields { max-width:54em;margin:0 auto 1.5em;padding:0 3.3%; }
                    #${remoteSectionId} .localIspNativeInputRow { display:flex;align-items:center;gap:.7em; }
                    #${remoteSectionId} .localIspNativeInputRow .inputContainer { flex:1;min-width:0;margin:0; }
                    @media(max-width:600px) { #${remoteSectionId} .localIspNativeInputRow { flex-wrap:wrap; } #${remoteSectionId} .localIspNativeInputRow .inputContainer { flex:1 1 75%; } }
                    #${remoteSectionId} .localIspNativeInputRow select { padding:.55em;background:#242424;color:inherit;border:1px solid #777;border-radius:.3em; }
                    #${remoteSectionId} .localIspResultSection { margin-top:1.6em; }
                    #${remoteSectionId} .localIspRemoteGrid { display:grid!important;grid-template-columns:repeat(auto-fill,minmax(10em,1fr));gap:1.75rem 1rem;padding:0 clamp(1rem,3vw,3rem); }
                    #${remoteSectionId} .localIspRemoteGrid > .localIspRemoteCard { display:block;width:100%!important;min-width:0!important;max-width:none!important;margin:0!important;padding:0!important; }
                    #${remoteSectionId} .localIspRemoteCard .cardBox { margin:0!important; }
                    #${remoteSectionId} .localIspRemoteCard .cardScalable { position:relative; }
                    #${remoteSectionId} .localIspRemoteCard .cardPadder { position:relative;box-sizing:border-box;aspect-ratio:2/3;height:auto;padding:0!important; }
                    #${remoteSectionId} .localIspRemoteCard .cardText:not(.localIspSource) { display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;white-space:normal;overflow:hidden;overflow-wrap:anywhere;line-height:1.25;min-height:2.5em;margin-top:.4em; }
                    #${remoteSectionId} .localIspPoster { display:block;position:absolute;inset:0;background-position:center;background-size:cover;background-color:rgba(255,255,255,.06);border-radius:.3em; }
                    #${remoteSectionId} .localIspPosterButton { position:absolute;inset:0;display:block;width:100%;height:100%;padding:0;border:0;border-radius:.3em;background:transparent;cursor:pointer; }
                    #${remoteSectionId} .localIspPosterButton:focus-visible { outline:2px solid #00a4dc;outline-offset:2px; }
                    #${remoteSectionId} .localIspPosterFallback { display:grid;place-items:center;color:rgba(255,255,255,.55);font-size:3em; }
                    #${remoteSectionId} .localIspSource { overflow:hidden;text-overflow:ellipsis;white-space:nowrap;opacity:.7;font-size:.86em; }
                    #${remoteSectionId} .localIspAddButton { width:100%;margin:.55em 0 0!important; }
                    #${remoteSectionId} .localIspSearchMessage { padding:0 3.3%;opacity:.75; }
                    #${remoteSectionId} .localIspTorrentTitle { width:100%;padding:0;border:0;background:transparent;color:inherit;font:inherit;cursor:pointer; }
                    @media(max-width:600px){#${remoteSectionId} .localIspRemoteGrid{grid-template-columns:repeat(2,minmax(0,1fr));gap:1rem .75rem;padding:0 1em}}
                `;
                document.head.appendChild(style);
            }

            function nativeSection() {
                let section = document.getElementById(remoteSectionId);
                if (section) return section;
                section = document.createElement('section');
                section.id = remoteSectionId;
                section.innerHTML = '<div class="localIspNativeFields"><div class="localIspNativeInputRow"><span class="material-icons search" aria-hidden="true"></span><div class="inputContainer"><input id="localIspNativeQuery" class="emby-input" type="text" placeholder="Search" autocomplete="off"></div><select id="localIspNativeMediaType" aria-label="Media type"><option value="">All media</option><option value="anime">Anime</option><option value="movie">Movies</option><option value="series">Shows</option><option value="other">Other</option></select><select id="localIspNativeSource" aria-label="Search source"><option value="">All sources</option><option value="ftp">FTP</option><option value="torrent">Torrent</option></select></div></div><div id="localIspCombinedResults" class="localIspResultSection"><h2 class="sectionTitle sectionTitle-cards focuscontainer-x padded-left padded-right">Search Results</h2><div class="localIspSearchMessage">Enter at least two characters to search your library and configured media servers.</div></div>';
                document.body.appendChild(section);
                return section;
            }

            function localPoster(item) {
                const tag = item.ImageTags?.Primary;
                return tag ? ApiClient.getScaledImageUrl(item.Id, { type:'Primary', maxWidth:360, tag }) : '';
            }

            function localCard(item) {
                const poster = posterButton(localPoster(item), item.Name);
                return `<div class="card overflowPortraitCard localIspRemoteCard"><div class="cardBox cardBox-bottompadded"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait">${poster}</div></div><div class="cardText cardTextCentered">${escapeHtml(item.Name)}</div><div class="cardText cardTextCentered localIspSource">${escapeHtml(item.Type)}${item.ProductionYear ? ' · ' + item.ProductionYear : ''}</div><button type="button" is="emby-button" class="raised button-submit localIspAddButton localIspPlayButton" data-id="${escapeHtml(item.Id)}"><span class="material-icons" aria-hidden="true">play_arrow</span><span>Play</span></button></div></div>`;
            }

            function posterButton(image, title) {
                const poster = image ? `<span class="localIspPoster" aria-hidden="true" style="background-image:url('${escapeHtml(image).replace(/'/g, '%27')}')"></span>`
                    : '<span class="localIspPoster localIspPosterFallback material-icons movie" aria-hidden="true"></span>';
                return `<button type="button" class="localIspPosterButton" aria-label="Open ${escapeHtml(title)}">${poster}</button>`;
            }

            function renderLocalResults(items) {
                nativeLocalItems = items;
                renderCombinedResults();
            }

            function remoteCard(item, index) {
                const poster = posterButton(item.posterUrl, item.name);
                const category = { movie:'Movie', series:'Show', anime:'Anime' }[item.mediaType];
                return `<div class="card overflowPortraitCard localIspRemoteCard" data-index="${index}">
                    <div class="cardBox cardBox-bottompadded"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait">${poster}</div></div>
                    <div class="cardText cardTextCentered">${escapeHtml(item.name)}</div><div class="cardText cardTextCentered localIspSource">${category} · ${escapeHtml(item.source)}</div>
                    <button is="emby-button" class="raised button-submit localIspAddButton" data-index="${index}"><span class="material-icons" aria-hidden="true">add</span><span>FTP</span></button></div></div>`;
            }

            function renderRemoteResults(items) {
                nativeRemoteItems = items;
                renderCombinedResults();
            }

            function renderCombinedResults() {
                const section = document.querySelector('#localIspCombinedResults');
                if (!section) return;
                const remoteItems = nativeRemoteItems;
                const localItems = nativeLocalItems;
                const visibleLocalItems = localItems.slice(0, 30);
                const cards = visibleLocalItems.map(localCard).join('') + remoteItems.map(remoteCard).join('');
                section.innerHTML = '<h2 class="sectionTitle sectionTitle-cards focuscontainer-x padded-left padded-right">Search Results</h2>'
                    + (cards || nativeTorrentGroups.length ? `<div class="localIspRemoteGrid">${cards}</div>` : '<div class="localIspSearchMessage">No matching titles found.</div>');
                renderNativeTorrents(section, visibleLocalItems, remoteItems);
                if (nativeRemoteStatus) {
                    const message = document.createElement('p');
                    message.className = 'localIspSearchMessage';
                    message.textContent = nativeRemoteStatus;
                    section.append(message);
                }
                section.querySelectorAll('.localIspPlayButton').forEach(button => {
                    const open = () => openIndexedItem(button.dataset.id);
                    button.onclick = async () => {
                        searchActions ??= import(ApiClient.getUrl('LocalIspFtpLoader/search-actions.js'));
                        const actions = await searchActions.catch(() => {
                            searchActions = null;
                            return null;
                        });
                        if (button.isConnected) {
                            const item = visibleLocalItems.find(entry => entry.Id === button.dataset.id);
                            return actions ? actions.playItem(item) : open();
                        }
                    };
                    button.closest('.cardBox').querySelector('.localIspPosterButton').onclick = open;
                });
                const buttons = section.querySelectorAll('.localIspAddButton[data-index]');
                if (!buttons.length) return;
                searchActions ??= import(ApiClient.getUrl('LocalIspFtpLoader/search-actions.js'));
                searchActions.then(actions => {
                    for (const button of buttons) {
                        if (!button.isConnected) continue;
                        const item = remoteItems[Number(button.dataset.index)];
                        actions.bind(button, item, localItems, pluginCall);
                        actions.bindPoster(button.closest('.cardBox').querySelector('.localIspPosterButton'), button, item.name);
                    }
                }).catch(() => { for (const button of buttons) button.textContent = 'Actions unavailable'; });
            }

            function renderNativeTorrents(section, localItems, remoteItems) {
                const grid = section.querySelector('.localIspRemoteGrid');
                const cards = grid?.querySelectorAll('.cardBox');
                const localTitles = localItems.map(item => ({title:item.Name, type:item.category, year:item.ProductionYear}));
                const remoteTitles = remoteItems.map(item => {
                    const year = item.name.match(/\s*[(\[]((?:19|20)\d{2})[)\]]\s*$/);
                    return {title:year ? item.name.slice(0, year.index) : item.name, type:item.mediaType, year:year ? Number(year[1]) : null};
                });
                const matched = new Map();
                for (const original of nativeTorrentGroups) {
                    const group = {...original, releases:[...original.releases]};
                    const localIndex = torrentActions.torrentMatch(group, localTitles);
                    const remoteIndex = torrentActions.torrentMatch(group, remoteTitles);
                    let target = cards[localIndex] ?? (remoteIndex >= 0 ? cards[localItems.length + remoteIndex] : null);
                    if (target && matched.has(target)) {
                        matched.get(target).releases.push(...group.releases);
                        continue;
                    }
                    const torrentOnly = !target;
                    if (torrentOnly) {
                        const card = document.createElement('div');
                        card.className = 'card overflowPortraitCard localIspRemoteCard';
                        const poster = posterButton(group.posterUrl, group.title);
                        card.innerHTML = `<div class="cardBox"><div class="cardScalable"><div class="cardPadder cardPadder-overflowPortrait">${poster}</div></div><button type="button" class="cardText cardTextCentered localIspTorrentTitle">${escapeHtml(group.title)}</button><div class="cardText cardTextCentered localIspSource">${escapeHtml([group.year, {movie:'Movie', series:'Show', anime:'Anime', adult:'Adult', other:'Other'}[group.type]].filter(Boolean).join(' · '))}</div></div>`;
                        grid.append(card);
                        target = card.firstElementChild;
                    }
                    if (group.type === 'other') {
                        target.querySelector('.localIspTorrentTitle').disabled = true;
                        continue;
                    }
                    matched.set(target, group);
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'raised button-submit localIspAddButton emby-button';
                    button.setAttribute('is', 'emby-button');
                    button.innerHTML = '<span class="material-icons" aria-hidden="true">add</span><span>Torrent</span>';
                    button.setAttribute('aria-label', (torrentOnly ? 'Add ' : 'Open ') + group.title + ' from a torrent');
                    button.onclick = () => {
                        const root = document.getElementById(remoteSectionId);
                        if (root) return torrentActions.prepareTorrent(group, root, pluginCall, button, torrentOnly);
                    };
                    target.append(button);
                    if (torrentOnly) {
                        target.querySelector('.localIspPosterButton').onclick = () => button.click();
                        target.querySelector('.localIspTorrentTitle').onclick = () => button.click();
                    }
                }
                if (nativeTorrentStatus) {
                    const message = document.createElement('p');
                    message.className = 'localIspSearchMessage';
                    message.textContent = nativeTorrentStatus;
                    section.append(message);
                }
            }

            async function searchNativeTorrents(query, sequence, filter) {
                try {
                    const result = await pluginCall('TorrentProviders/Search', 'POST', { query, mediaType:filter });
                    if (sequence !== nativeSearchSequence) return;
                    if (value(result, 'Releases')?.length) {
                        torrentSearchModule ??= import(ApiClient.getUrl('LocalIspFtpLoader/torrent-search.js'));
                        const actions = await torrentSearchModule;
                        if (sequence !== nativeSearchSequence) return;
                        torrentActions = actions;
                        nativeTorrentGroups = actions.groupTorrents(result);
                    }
                    const failed = value(result, 'FailedProviders');
                    const providers = value(result, 'ProviderCount');
                    if (failed) nativeTorrentStatus = failed === providers ? 'Torrent providers could not be searched.' : 'Some torrent providers could not be searched.';
                    else if (filter === 'adult' && providers === 0) nativeTorrentStatus = 'Add an adult-capable torrent provider in Setup Media, such as 0Magnet.';
                    else if (filter === 'other' && providers === 0) nativeTorrentStatus = 'Enable torrent sources and add a Torznab provider that supports Other in Setup Media.';
                    else if (providers && !nativeTorrentGroups.length) nativeTorrentStatus = 'No matching torrent releases found.';
                } catch (error) {
                    torrentSearchModule = null;
                    if (sequence !== nativeSearchSequence) return;
                    nativeTorrentStatus = error.message || 'Torrent results could not be loaded.';
                }
                if (sequence === nativeSearchSequence) renderCombinedResults();
            }

            async function searchRemoteSources(query, sequence, filter) {
                const section = document.querySelector('#localIspCombinedResults');
                if (!section) return;
                if (query.length < 2) {
                    section.innerHTML = '<h2 class="sectionTitle sectionTitle-cards focuscontainer-x padded-left padded-right">Search Results</h2><div class="localIspSearchMessage">Enter at least two characters to search.</div>';
                    return;
                }
                section.innerHTML = '<h2 class="sectionTitle sectionTitle-cards focuscontainer-x padded-left padded-right">Search Results</h2><div class="localIspSearchMessage">Searching media sources…</div>';
                const mediaTypes = filter ? [filter] : ['movie', 'series', 'anime'];
                const settled = await Promise.allSettled(mediaTypes.map((mediaType) => pluginCall('Remote/Search', 'POST', { mediaType, query })
                    .then((rows) => rows.map((row) => ({ name:value(row, 'Name'), url:value(row, 'Url'), source:value(row, 'Source'), posterUrl:value(row, 'PosterUrl'), mediaType })) )));
                if (sequence !== nativeSearchSequence || !window.location.hash.startsWith('#/search')) return;
                const results = settled.flatMap((result) => result.status === 'fulfilled' ? result.value : [])
                    .filter((item, index, all) => all.findIndex((other) => other.url === item.url && other.mediaType === item.mediaType) === index);
                if (settled.every((result) => result.status === 'rejected')) {
                    nativeRemoteStatus = settled[0].reason.message || 'Media servers could not be searched.';
                }
                renderRemoteResults(results);
            }

            async function searchLocalLibrary(query, sequence, filter) {
                const [actions, libraries] = await Promise.all([
                    import(ApiClient.getUrl('LocalIspFtpLoader/search-actions.js')), pluginCall('Libraries').catch(() => [])
                ]);
                const items = await actions.searchLibrary(query, filter, libraries);
                if (sequence !== nativeSearchSequence || !window.location.hash.startsWith('#/search')) return;
                renderLocalResults(items);
            }

            function openIndexedItem(id) {
                if (window.Dashboard?.navigate) Dashboard.navigate('details?id=' + encodeURIComponent(id));
                else window.location.hash = '#/details?id=' + encodeURIComponent(id);
            }

            function attachNativeSearch() {
                if (!window.location.hash.startsWith('#/search')) {
                    document.getElementById(remoteSectionId)?.remove();
                    if (nativeSearchInput) ++nativeSearchSequence;
                    clearTimeout(nativeSearchTimer);
                    nativeSearchInput = null;
                    return;
                }
                ensureNativeSearchStyles();
                nativeSection();
                const input = document.querySelector('#localIspNativeQuery');
                if (!input || input === nativeSearchInput) return;
                nativeSearchInput = input;
                const select = document.querySelector('#localIspNativeMediaType');
                const source = document.querySelector('#localIspNativeSource');
                source.value = nativeSource;
                select.value = nativeMediaType;
                if (!select.value) nativeMediaType = '';
                canManageTorrents ??= pluginCall('CanManage').then(() => true, () => false);
                canManageTorrents.then(async (canManage) => {
                    if (!canManage) return;
                    const availability = await pluginCall('SourceAvailability');
                    if (select.isConnected && value(availability, 'TorrentEnabled') && value(availability, 'AdultTorrentModeEnabled')) {
                        const option = document.createElement('option');
                        option.value = 'adult';
                        option.textContent = 'Adult torrents';
                        select.append(option);
                    }
                }).catch(() => {});
                const search = (delay) => {
                    clearTimeout(nativeSearchTimer);
                    const sequence = ++nativeSearchSequence;
                    nativeSearchTimer = setTimeout(() => {
                        const query = input.value.trim();
                        nativeLocalItems = [];
                        nativeRemoteItems = [];
                        nativeRemoteStatus = '';
                        nativeTorrentGroups = [];
                        nativeTorrentStatus = '';
                        const torrentOnly = nativeMediaType === 'adult' || nativeMediaType === 'other';
                        if (!nativeSource && !torrentOnly && query.length >= 2) searchLocalLibrary(query, sequence, nativeMediaType).catch(() => {});
                        if ((!torrentOnly && nativeSource !== 'torrent') || query.length < 2) searchRemoteSources(query, sequence, nativeMediaType);
                        else renderCombinedResults();
                        if (query.length >= 2 && nativeSource !== 'ftp') searchNativeTorrents(query, sequence, nativeMediaType);
                    }, delay);
                };
                input.addEventListener('input', () => search(450));
                select.addEventListener('change', () => {
                    nativeMediaType = select.value;
                    if (nativeSource === 'ftp' && ['adult', 'other'].includes(nativeMediaType)) source.value = nativeSource = 'torrent';
                    search(0);
                });
                source.addEventListener('change', () => {
                    nativeSource = source.value;
                    if (nativeSource === 'ftp' && ['adult', 'other'].includes(nativeMediaType)) select.value = nativeMediaType = '';
                    search(0);
                });
                setTimeout(() => input.focus(), 50);
            }

            let scheduled = false;
            const ensureEnhancements = () => {
                if (scheduled) return;
                scheduled = true;
                requestAnimationFrame(() => {
                    scheduled = false;
                    ensureButton();
                    ensureUserMenuItem();
                    ensureSidebarMenu();
                    applySearchReplacement();
                    attachNativeSearch();
                });
            };
            const observer = new MutationObserver(ensureEnhancements);

            function start() {
                ensureEnhancements();
                loadSettings();
                window.addEventListener('hashchange', ensureEnhancements);
                observer.observe(document.body, { childList: true, subtree: true });
            }

            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', start, { once: true });
            } else {
                start();
            }
        })();
        """;
}
