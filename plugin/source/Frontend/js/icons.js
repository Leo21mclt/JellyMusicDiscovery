// Music Discovery — overlay icon for stub items + click-to-request hooks.
//
// Loaded by Jellyfin's web client because the file is registered as an embedded
// resource. The script polls the DOM for item cards that the server tagged with
// `mdiscover-stub` and:
//   1. overlays a cloud-download icon
//   2. for *album* stubs, hijacks the play button to instead POST /mdiscover/request/album/{mbid}
//
// We don't override track stub playback — those resolve naturally via the
// IMediaSourceProvider on the server, so a normal "play" works.

(function () {
  const STUB_TAG = 'mdiscover-stub';
  const REQUESTED_TAG = 'mdiscover-requested';
  const POLL_INTERVAL_MS = 800;

  function getApi() {
    return window.ApiClient ?? null;
  }

  function pluginRequestUrl(itemId) {
    return getApi()?.getUrl(`mdiscover/request/album/${itemId}`) ?? `/mdiscover/request/album/${itemId}`;
  }

  function decorate(card, item) {
    if (card.dataset.mdiscoverDecorated === '1') return;
    card.dataset.mdiscoverDecorated = '1';

    const overlay = document.createElement('div');
    overlay.className = 'mdiscover-overlay';
    overlay.title = item.tags?.includes(REQUESTED_TAG) ? 'Requested — pending download' : 'Available to request';
    overlay.style.cssText = [
      'position:absolute','top:6px','right:6px','width:28px','height:28px',
      'border-radius:50%','background:rgba(0,0,0,0.55)','display:flex',
      'align-items:center','justify-content:center','color:#fff','font-size:16px',
      'pointer-events:none','z-index:5'
    ].join(';');
    overlay.textContent = item.tags?.includes(REQUESTED_TAG) ? '⏳' : '☁︎';
    card.style.position = card.style.position || 'relative';
    card.appendChild(overlay);

    if ((item.type === 'MusicAlbum' || item.Type === 'MusicAlbum')) {
      hijackAlbumPlay(card, item);
    }
  }

  function hijackAlbumPlay(card, item) {
    const playBtn = card.querySelector('.itemAction[data-action="play"], .playButton, [data-action="resume"]');
    if (!playBtn) return;
    playBtn.addEventListener('click', async (e) => {
      if (!item.id) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      try {
        await fetch(pluginRequestUrl(item.id), { method: 'POST', credentials: 'include' });
        toast('Requested — Lidarr is on it.');
      } catch (err) {
        toast('Request failed: ' + err.message);
      }
    }, { capture: true });
  }

  function toast(msg) {
    const el = document.createElement('div');
    el.textContent = msg;
    el.style.cssText = 'position:fixed;bottom:24px;left:50%;transform:translateX(-50%);padding:10px 16px;background:#222;color:#fff;border-radius:6px;z-index:10000;font-size:14px;';
    document.body.appendChild(el);
    setTimeout(() => el.remove(), 3500);
  }

  async function scan() {
    const api = getApi();
    if (!api) return;

    const cards = document.querySelectorAll('.card[data-id], [data-id].card');
    if (cards.length === 0) return;

    const idsToFetch = [];
    const pending = [];
    cards.forEach(c => {
      if (c.dataset.mdiscoverDecorated === '1') return;
      const id = c.dataset.id;
      if (!id) return;
      idsToFetch.push(id);
      pending.push(c);
    });
    if (idsToFetch.length === 0) return;

    try {
      const r = await api.getItems(api.getCurrentUserId(), {
        Ids: idsToFetch.join(','),
        Fields: 'Tags,ProviderIds',
      });
      const byId = new Map((r?.Items ?? []).map(i => [i.Id, i]));
      pending.forEach(card => {
        const item = byId.get(card.dataset.id);
        if (!item || !(item.Tags ?? []).includes(STUB_TAG)) return;
        decorate(card, {
          id: item.Id,
          type: item.Type,
          tags: item.Tags,
          providerIds: item.ProviderIds,
        });
      });
    } catch (e) {
      // Soft-fail; we'll try again on next poll.
    }
  }

  setInterval(scan, POLL_INTERVAL_MS);
})();
