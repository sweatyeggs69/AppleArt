const HISTORY_LIMIT = 18;

const ui = {
    historyContainer: document.getElementById('historyContainer'),
    historyList: document.getElementById('historyList'),
    historyFadeWrapper: document.getElementById('historyFadeWrapper')
};

let historyHlsInstances = [];

async function fetchSystemStatus() {
    const statsContainer = document.getElementById('statsContainer');
    const statSearches = document.getElementById('statSearches');
    const statAnimated = document.getElementById('statAnimatedEntries');
    const statCache = document.getElementById('statCacheEntries');

    try {
        const res = await fetch('/api/v1/status');
        const data = await res.json();

        statSearches.textContent = data.totalSearches.toLocaleString() || '0';
        statAnimated.textContent = data.totalAnimatedEntries.toLocaleString() || '0';
        statCache.textContent = data.totalCacheEntries.toLocaleString() || '0';

        statsContainer.classList.remove('hidden');
        statsContainer.classList.add('flex');
    } catch (e) {
        statsContainer.classList.add('hidden');
        statsContainer.classList.remove('flex');
    }
}

function attachPreview(video, index) {
    const url = video.dataset.url;
    if (Hls.isSupported()) {
        const hls = new Hls({ capLevelToPlayerSize: true, autoStartLoad: true });
        hls.loadSource(url);
        hls.attachMedia(video);
        hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => {}));
        historyHlsInstances[index] = hls;
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        video.src = url;
        video.addEventListener('loadedmetadata', () => video.play().catch(() => {}));
        historyHlsInstances[index] = true;
    }
}

async function fetchGlobalHistory() {
    try {
        const response = await fetch('/api/v1/artwork/history');
        if (!response.ok) return;

        const historyData = (await response.json()).slice(0, HISTORY_LIMIT);
        if (historyData.length === 0) return;

        ui.historyContainer.classList.remove('hidden');
        ui.historyList.replaceChildren();
        ui.historyFadeWrapper.classList.toggle('history-fade-wrapper', historyData.length >= HISTORY_LIMIT);

        historyHlsInstances.forEach(hls => { if (hls && hls !== true) hls.destroy(); });
        historyHlsInstances = new Array(historyData.length).fill(null);

        const observer = new IntersectionObserver(entries => {
            entries.forEach(entry => {
                const video = entry.target;
                const index = Number(video.dataset.index);
                if (!entry.isIntersecting) {
                    video.pause();
                } else if (!historyHlsInstances[index]) {
                    attachPreview(video, index);
                } else {
                    video.play().catch(() => {});
                }
            });
        }, { rootMargin: '50px' });

        historyData.forEach((item, index) => {
            const li = document.createElement('li');
            li.className = 'glass-panel p-2 rounded-lg history-item flex items-center gap-3';

            const frame = document.createElement('div');
            frame.className = 'w-12 h-12 flex-shrink-0 rounded bg-gray-800 border border-gray-700 overflow-hidden relative shadow-inner';
            const video = document.createElement('video');
            video.dataset.index = index;
            video.dataset.url = item.url || item.url_tall || '';
            video.className = 'w-full h-full object-cover';
            video.loop = true;
            video.muted = true;
            video.playsInline = true;
            frame.appendChild(video);

            const text = document.createElement('div');
            text.className = 'truncate flex-grow';
            const album = document.createElement('p');
            album.className = 'font-bold text-sm text-gray-200 truncate';
            album.textContent = item.album;
            const artist = document.createElement('p');
            artist.className = 'text-xs text-gray-400 truncate';
            artist.textContent = item.artist;
            text.append(album, artist);

            const time = document.createElement('div');
            time.className = 'text-xs text-gray-500 whitespace-nowrap ml-2 pr-2';
            time.textContent = new Date(item.fetchedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

            li.append(frame, text, time);
            ui.historyList.appendChild(li);
            observer.observe(video);
        });
    } catch (error) {
        console.error("Failed to fetch history:", error);
    }
}

fetchGlobalHistory();
fetchSystemStatus();
setInterval(fetchSystemStatus, 30000);
