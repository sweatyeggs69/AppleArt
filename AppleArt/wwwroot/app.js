const ui = {
    form: document.getElementById('searchForm'),
    submitBtn: document.getElementById('submitBtn'),
    spinner: document.getElementById('loadingSpinner'),
    statusMessage: document.getElementById('statusMessage'),
    videoContainer: document.getElementById('videoContainer'),
    videoElement: document.getElementById('artworkVideo'),
    rawLink: document.getElementById('rawLink'),
    historyContainer: document.getElementById('historyContainer'),
    historyList: document.getElementById('historyList'),
    tabDetails: document.getElementById('tabDetails'),
    tabUrl: document.getElementById('tabUrl'),
    groupDetails: document.getElementById('groupDetails'),
    groupUrl: document.getElementById('groupUrl'),
    artworkMetadata: document.getElementById('artworkMetadata'),
    metaAlbum: document.getElementById('metaAlbum'),
    metaArtist: document.getElementById('metaArtist'),
    cacheBadge: document.getElementById('cacheBadge'),
    variantSelector: document.getElementById('variantSelector'),
    variantSquareBtn: document.getElementById('variantSquareBtn'),
    variantTallBtn: document.getElementById('variantTallBtn'),
    coverVariantSelected: document.getElementById('coverVariantSelected'),
    selectedVariantBadge: document.getElementById('selectedVariantBadge')
};

let state = {
    currentMode: 'details',
    mainHls: null,
    historyHlsInstances: [],
    currentM3u8Url: null,
    currentAlbumName: null,
    currentArtworkVariants: {
        square: null,
        tall: null
    },
    selectedArtworkVariant: 'square'
};

function setMode(mode) {
    state.currentMode = mode;
    const activeClass = "flex-1 py-2 text-sm font-medium rounded-lg transition-all bg-gradient-to-r from-pink-600 to-orange-500 text-white shadow-lg";
    const inactiveClass = "flex-1 py-2 text-sm font-medium rounded-lg transition-all text-gray-400 hover:text-white";

    if (mode === 'details') {
        ui.tabDetails.className = activeClass;
        ui.tabUrl.className = inactiveClass;
        ui.groupDetails.classList.remove('hidden');
        ui.groupUrl.classList.add('hidden');
    } else {
        ui.tabUrl.className = activeClass;
        ui.tabDetails.className = inactiveClass;
        ui.groupUrl.classList.remove('hidden');
        ui.groupDetails.classList.add('hidden');
    }
}

function showError(msg) {
    ui.statusMessage.textContent = msg;
    ui.statusMessage.className = "mt-4 text-center text-sm text-red-400";
    ui.statusMessage.classList.remove('hidden');
    ui.submitBtn.disabled = false;
    ui.spinner.classList.add('hidden');
}

function updateMetadataUI(data) {
    ui.artworkMetadata.classList.remove('hidden');
    ui.metaAlbum.textContent = data.album;
    ui.metaArtist.textContent = data.artist;

    if (data.isCached) {
        ui.cacheBadge.classList.remove('hidden');
        ui.cacheBadge.classList.add('flex');
    } else {
        ui.cacheBadge.classList.add('hidden');
        ui.cacheBadge.classList.remove('flex');
    }
}

function getArtworkVariantLabel(variant) {
    return variant === 'tall' ? 'Tall Cover' : 'Square Cover';
}

function getArtworkVariantShortLabel(variant) {
    return variant === 'tall' ? 'Tall' : 'Square';
}

function updateVariantBadge() {
    if (ui.coverVariantSelected) {
        ui.coverVariantSelected.textContent = getArtworkVariantShortLabel(state.selectedArtworkVariant);
    }

    if (ui.selectedVariantBadge) {
        ui.selectedVariantBadge.textContent = getArtworkVariantLabel(state.selectedArtworkVariant);
    }
}

function updatePreviewAspect(variant) {
    if (!ui.videoContainer) {
        return;
    }

    const isTall = variant === 'tall';
    ui.videoContainer.classList.toggle('cover-frame-square', !isTall);
    ui.videoContainer.classList.toggle('cover-frame-tall', isTall);
}

function updateVariantSelector() {
    if (!ui.variantSelector) {
        return;
    }

    const hasSquare = Boolean(state.currentArtworkVariants.square);
    const hasTall = Boolean(state.currentArtworkVariants.tall);
    const canSwitch = hasSquare && hasTall;

    ui.variantSelector.classList.toggle('hidden', !canSwitch);

    const activeButtonClass = 'variant-option variant-option-active rounded-lg px-3 py-2 text-left is-active';
    const inactiveButtonClass = 'variant-option variant-option-inactive rounded-lg px-3 py-2 text-left';
    const disabledButtonClass = 'variant-option variant-option-inactive rounded-lg px-3 py-2 text-left opacity-45 cursor-not-allowed';

    if (ui.variantSquareBtn) {
        ui.variantSquareBtn.disabled = !hasSquare;
        ui.variantSquareBtn.className = hasSquare
            ? (state.selectedArtworkVariant === 'square' ? activeButtonClass : inactiveButtonClass)
            : disabledButtonClass;
    }

    if (ui.variantTallBtn) {
        ui.variantTallBtn.disabled = !hasTall;
        ui.variantTallBtn.className = hasTall
            ? (state.selectedArtworkVariant === 'tall' ? activeButtonClass : inactiveButtonClass)
            : disabledButtonClass;
    }

    updateVariantBadge();
}

async function selectArtworkVariant(variant, playPreview = true) {
    const preferredVariant = state.currentArtworkVariants[variant] ? variant : (state.currentArtworkVariants.square ? 'square' : state.currentArtworkVariants.tall ? 'tall' : null);

    if (!preferredVariant) {
        return null;
    }

    const selectedUrl = state.currentArtworkVariants[preferredVariant];

    if (state.selectedArtworkVariant === preferredVariant && state.currentM3u8Url === selectedUrl) {
        updateVariantSelector();
        updatePreviewAspect(preferredVariant);

        if (playPreview) {
            playVideo(selectedUrl);
        }

        return selectedUrl;
    }

    state.selectedArtworkVariant = preferredVariant;
    state.currentM3u8Url = selectedUrl;

    updateVariantSelector();
    updatePreviewAspect(preferredVariant);

    if (playPreview) {
        playVideo(selectedUrl);
    }

    return selectedUrl;
}

async function applyArtworkData(data, isCached = false) {
    state.currentArtworkVariants = {
        square: data.url ?? null,
        tall: data.url_tall ?? null
    };
    state.currentAlbumName = data.album;

    updateMetadataUI({
        album: data.album,
        artist: data.artist,
        isCached
    });

    const preferredVariant = data.url ? 'square' : 'tall';
    await selectArtworkVariant(preferredVariant, true);
}

function playVideo(url) {
    ui.statusMessage.classList.add('hidden');
    ui.videoContainer.classList.remove('hidden');
    ui.rawLink.href = url;
    ui.rawLink.textContent = url;
    ui.rawLink.classList.remove('hidden');

    if (Hls.isSupported()) {
        if (state.mainHls) state.mainHls.destroy();
        state.mainHls = new Hls();
        state.mainHls.loadSource(url);
        state.mainHls.attachMedia(ui.videoElement);
        state.mainHls.on(Hls.Events.MANIFEST_PARSED, () => {
            ui.videoElement.play().catch(e => console.log("Autoplay prevented:", e));
        });
    } else if (ui.videoElement.canPlayType('application/vnd.apple.mpegurl')) {
        ui.videoElement.src = url;
        ui.videoElement.addEventListener('loadedmetadata', () => {
            ui.videoElement.play().catch(e => console.log("Autoplay prevented:", e));
        });
    }
}

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

async function fetchGlobalHistory() {
    try {
        const response = await fetch('/api/v1/artwork/history');
        if (!response.ok) return;

        const historyData = await response.json();
        if (historyData.length === 0) return;

        const fadeWrapper = document.getElementById('historyFadeWrapper');
        ui.historyContainer.classList.remove('hidden');
        ui.historyList.innerHTML = '';

        if (historyData.length >= 12) {
            fadeWrapper.classList.add('history-fade-wrapper');
        } else {
            fadeWrapper.classList.remove('history-fade-wrapper');
        }
        
        state.historyHlsInstances.forEach(hls => { if (hls) hls.destroy(); });
        state.historyHlsInstances = new Array(historyData.length).fill(null);
        
        const observer = new IntersectionObserver((entries) => {
            entries.forEach(entry => {
                const video = entry.target;
                const index = video.getAttribute('data-index');
                const url = video.getAttribute('data-url');

                if (entry.isIntersecting) {
                    if (!state.historyHlsInstances[index]) {
                        if (Hls.isSupported()) {
                            const thumbHls = new Hls({ capLevelToPlayerSize: true, autoStartLoad: true });
                            thumbHls.loadSource(url);
                            thumbHls.attachMedia(video);
                            thumbHls.on(Hls.Events.MANIFEST_PARSED, () => {
                                video.play().catch(() => {});
                            });
                            state.historyHlsInstances[index] = thumbHls;
                        } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
                            video.src = url;
                            video.addEventListener('loadedmetadata', () => {
                                video.play().catch(() => {});
                            });
                        }
                    } else {
                        video.play().catch(() => {});
                    }
                } else {
                    video.pause();
                }
            });
        }, {
            rootMargin: '50px'
        });

        historyData.forEach((item, index) => {
            const previewUrl = item.url || item.url_tall || '';
            const li = document.createElement('li');
            li.className = 'glass-panel p-2 rounded-lg history-item flex items-center gap-3 transition-colors cursor-pointer hover:bg-white/5';
            li.innerHTML = `
                <div class="w-12 h-12 flex-shrink-0 rounded bg-gray-800 border border-gray-700 overflow-hidden relative shadow-inner">
                    <video id="hist-vid-${index}" data-index="${index}" data-url="${previewUrl}" class="w-full h-full object-cover" loop muted playsinline></video>
                </div>
                <div class="truncate flex-grow">
                    <p class="font-bold text-sm text-gray-200 truncate">${item.album}</p>
                    <p class="text-xs text-gray-400 truncate">${item.artist}</p>
                </div>
                <div class="text-xs text-gray-500 whitespace-nowrap ml-2 pr-2">
                    ${new Date(item.fetchedAt).toLocaleTimeString([], {hour: '2-digit', minute:'2-digit'})}
                </div>
            `;

            li.onclick = () => {
                document.getElementById('artistInput').value = item.artist;
                document.getElementById('albumInput').value = item.album;

                void applyArtworkData(item, true);
                
                requestAnimationFrame(() => {
                    ui.videoContainer.scrollIntoView({
                        behavior: 'smooth',
                        block: 'center'
                    });
                });
            };

            ui.historyList.appendChild(li);
            
            const thumbnailVideo = document.getElementById(`hist-vid-${index}`);
            observer.observe(thumbnailVideo);
        });
    } catch (error) {
        console.error("Failed to fetch history:", error);
    }
}

ui.tabDetails.onclick = () => setMode('details');
ui.tabUrl.onclick = () => setMode('url');
if (ui.variantSquareBtn) {
    ui.variantSquareBtn.addEventListener('click', () => {
        void selectArtworkVariant('square', true);
    });
}
if (ui.variantTallBtn) {
    ui.variantTallBtn.addEventListener('click', () => {
        void selectArtworkVariant('tall', true);
    });
}
ui.form.addEventListener('submit', async (e) => {
    e.preventDefault();

    ui.statusMessage.classList.add('hidden');
    ui.videoContainer.classList.add('hidden');
    ui.rawLink.classList.add('hidden');
    ui.artworkMetadata.classList.add('hidden');
    ui.submitBtn.disabled = true;
    ui.spinner.classList.remove('hidden');

    let apiUrl = '';

    if (state.currentMode === 'details') {
        const artist = document.getElementById('artistInput').value.trim();
        const album = document.getElementById('albumInput').value.trim();
        const title = document.getElementById('titleInput').value.trim();

        if (!artist || !album) return showError("Please enter both Artist and Album.");

        const queryParams = { artist, album };
        if (title) queryParams.title = title;
        apiUrl = `/api/v1/artwork/search?${new URLSearchParams(queryParams)}`;
    } else {
        const url = document.getElementById('urlInput').value.trim();
        if (!url || !url.includes('music.apple.com')) return showError("Please enter a valid Apple Music URL.");
        apiUrl = `/api/v1/artwork/url?${new URLSearchParams({ url })}`;
    }

    try {
        const response = await fetch(apiUrl);
        if (!response.ok) {
            if (response.status === 404) throw new Error('No animated artwork found.');
            throw new Error('Server error occurred.');
        }

        const data = await response.json();
        
        await applyArtworkData(data, data.isCached);
        fetchGlobalHistory();

    } catch (error) {
        showError(error.message);
    } finally {
        ui.submitBtn.disabled = false;
        ui.spinner.classList.add('hidden');
    }
});


fetchGlobalHistory();
fetchSystemStatus();
setInterval(fetchSystemStatus, 30000);