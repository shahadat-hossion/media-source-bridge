const value=(item,name)=>item?.[name[0].toLowerCase()+name.slice(1)]??item?.[name];
const titleKey=title=>String(title??'').normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}]+/gu,'');
export function groupTorrents(result){
    const fields=['Title','Year','Type','Release','Provider','Resolution','Size','Seeders','Magnet','PosterUrl'];
    const releases=(value(result,'Releases')??[]).map(release=>Object.fromEntries(fields.map(field=>[field[0].toLowerCase()+field.slice(1),value(release,field)])));
    const groups=new Map();
    for(const release of releases){
        const key=JSON.stringify([titleKey(release.title),release.year??null,release.type]);
        if(!groups.has(key))groups.set(key,{title:release.title,year:release.year,type:release.type,releases:[]});
        const group=groups.get(key);
        group.posterUrl??=release.posterUrl;
        if(!group.releases.some(item=>item.provider===release.provider && item.release===release.release))group.releases.push(release);
    }
    const titles=[...groups.values()];
    return titles.filter(group=>{
        if(group.year)return true;
        const matches=titles.filter(candidate=>candidate.year && candidate.type===group.type && titleKey(candidate.title)===titleKey(group.title));
        if(matches.length!==1)return true;
        matches[0].releases.push(...group.releases);
        matches[0].posterUrl??=group.posterUrl;
        return false;
    });
}
export function torrentMatch(group,items){
    const matches=items.map((item,index)=>({item,index})).filter(({item})=>
        titleKey(item.title)===titleKey(group.title) && (!group.type || item.type===group.type) &&
        (!item.year || !group.year || item.year===group.year));
    return matches.length===1?matches[0].index:-1;
}

let preparation;

function showOpeningGlow(button){
    if(!button)return;
    if(!document.getElementById('torrentOpeningStyles')){
        const style=document.createElement('style');
        style.id='torrentOpeningStyles';
        style.textContent=`
            .torrentOpening[aria-busy="true"] {
                animation:torrentOpeningGlow 1.5s ease-in-out infinite;
            }
            @keyframes torrentOpeningGlow {
                0%,100% { box-shadow:0 0 0 .08rem #72dcff, 0 0 .4rem #00a4dc88; }
                50% { box-shadow:0 0 0 .15rem #c7f4ff, 0 0 1.25rem .2rem #00b9ffb3; }
            }
            @media(prefers-reduced-motion:reduce) {
                .torrentOpening[aria-busy="true"] { animation:none;box-shadow:0 0 0 .12rem #72dcff; }
            }
        `;
        document.head.append(style);
    }
    button.classList.add('torrentOpening');
    button.setAttribute('aria-busy','true');
}

export async function prepareTorrent(group,root,call,button){
    if(preparation)return;
    let status=root.querySelector('.torrentPreparationStatus');
    if(!status){
        status=document.createElement('p');
        status.className='torrentPreparationStatus';
        status.setAttribute('role','status');
        status.style.cssText='padding:.65rem 1rem;margin:.5rem 1rem;background:#232f43;border-radius:.35rem';
        root.prepend(status);
    }
    status.textContent='Checking which releases can stream '+group.title+'…';
    const releases=group.releases.filter(release=>release.magnet).slice(0,50).map(({magnet,size,seeders,resolution})=>
        ({magnet,size,seeders,resolution}));
    if(!releases.length){status.textContent='No release has a compatible magnet.';return}
    showOpeningGlow(button);
    preparation=call('PrepareTorrent','POST',{title:group.title,year:group.year,mediaType:group.type,releases});
    try{
        const result=await preparation;
        const warning=value(result,'Status')==='mayBuffer'?'Playback may buffer. ':'';
        status.textContent=warning+'Stream ready. Adding '+group.title+' to the library…';
        const actions=await import(ApiClient.getUrl('LocalIspFtpLoader/search-actions.js'));
        const item=await actions.waitForIndexedItem(group.type,value(result,'Added'));
        if(!item){
            status.textContent='The STRM file is saved. Jellyfin is still indexing this title.';
            return;
        }
        if(!root.isConnected)return;
        status.textContent='Added '+group.title+' to the library.';
        button.innerHTML='<span class="material-icons" aria-hidden="true">open_in_new</span><span>Open title</span>';
        button.setAttribute('aria-label','Open '+group.title);
        button.onclick=()=>actions.openItem(item);
        actions.openItem(item);
    }catch(error){status.textContent=error.message}
    finally{
        preparation=null;
        button?.classList.remove('torrentOpening');
        button?.removeAttribute('aria-busy');
    }
}
