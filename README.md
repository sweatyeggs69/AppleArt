# [<img src="AppleArt/wwwroot/assets/logo.png" width="40" alt="" style="vertical-align: middle;">](https://amaa.tromme.app) AppleArt

[![Website](https://img.shields.io/badge/Public_Instance-blue?style=for-the-badge)](https://amaa.tromme.app)
[![Build Status](https://img.shields.io/github/actions/workflow/status/sweatyeggs69/AppleArt/docker-publish.yml?style=for-the-badge)](https://github.com/sweatyeggs69/AppleArt/actions)

A lightweight tool to fetch and display Apple Music’s animated album covers (HLS/m3u8) and static album covers (1000x1000). Supports both square and tall animated variants, as well as playlist artworks. Built with .NET 10 and a minimal Tailwind CSS frontend. Powers animated artwork in [Tromme](https://github.com/sweatyeggs69/Tromme).

**Test it out:** [https://amaa.tromme.app](https://amaa.tromme.app)

## What it does
- Search: Accepts artist and album names, or Apple Music URLs, to find the corresponding animated artwork.
- Catalog: Every lookup (animated, static, artist, album details) goes through the Apple Music catalog API.
- Multiple Variants: Supports both the standard square cover and the taller variant used for lock screen & expanded artwork views.
- API: Provides a simple REST API for programmatic access to the artwork data.
- Cache: Saves results in local cache files. It only hits Apple's servers once per album.
- Web UI: Minimal page showing the most recently cached animated artwork and cache stats.

## API Reference

Base URL: https://amaa.tromme.app

**Get Artwork by Details**

```GET /api/v1/artwork/search?artist=Linkin+Park&album=Living+Things```

**Get Artwork by URL**

```GET /api/v1/artwork/url?url=https://music.apple.com/us/album/...```

**Get Static Artwork by Details**

```GET /api/v1/artwork/static/search?artist=Gorillaz&album=Plastic+Beach```

**Get Static Artwork by URL** (albums only)

```GET /api/v1/artwork/static/url?url=https://music.apple.com/us/album/...```

Both return `results`, closest album title first, each with `url` (1000x1000), `thumbnail` (600x600), `artist`, `album`, `appleMusicUrl` and `releaseDate`. Lookups go to the Apple Music catalog and are saved in the SQLite cache like animated artwork: found results are kept, no-matches expire after 30 days (exact query only), and count against the same Apple lookup limits.

**Get Artist Images**

```GET /api/v1/artwork/artist/search?artist=Gorillaz```

Same response shape (empty `album`, no `releaseDate`, plus `logo`: a transparent HEIC wordmark when Apple has one, and `video`: the square motion artist HLS playlist when Apple has one), from the Apple Music catalog, with the same caching and limits, except artist results (photo, logo and video) are rechecked with Apple after 30 days.

**Get Album Details**

```GET /api/v1/album/details?id=1440935467&country=us```

The album (`title`, `artist`, `releaseDate`, `genre`, `copyright`, `artwork` at 1400x1400) and its `tracks` (`title`, `artist`, `trackNumber`, `discNumber`, `durationMs`) from the Apple Music catalog. `id` is the number at the end of an `appleMusicUrl`, `country` defaults to `us`. Kept in memory for a day (no-matches for an hour) and counted against the Apple lookup limits.

**Get Global History**

```GET /api/v1/artwork/history```

## Origin Secret
Set `ORIGIN_SECRET` to make `/api` require a matching `X-Origin-Secret` header (the Tromme API Worker adds it). Unset, the API is open.

Requests that have to reach Apple (cache misses) are limited to `APPLE_LOOKUPS_PER_CLIENT_PER_MINUTE` (default 20) per client and `APPLE_LOOKUPS_PER_MINUTE` (default 120) overall. Clients are identified by `CF-Connecting-IP` when the connection comes from a local or private address (the Cloudflare tunnel), otherwise by the connecting address; `X-Forwarded-For` is ignored. Requests relayed by the Tromme API Worker all share the Worker's `CF-Connecting-IP`, so when `PROXY_SECRET` is set and a request's `X-Origin-Secret` matches it, the Worker's `X-Client-IP` header identifies the client instead. Set `PROXY_SECRET` to the Worker's `ARTWORK_ORIGIN_SECRET`; unlike `ORIGIN_SECRET`, it never blocks direct callers.

## Caching Strategy
This project is designed to be "Apple-friendly" to avoid rate limits:
1. **Edition Matching:** Searches pick the closest album title, so an exact match beats editions such as "Deluxe" or "Anniversary". Cached lookups match the normalized album name exactly.
2. Negative Artwork Caching: Albums that are confirmed to have no animated artwork are cached as NONE to prevent repeated futile scraping.
3. Negative Search Caching: If a search query returns no matches, that result is cached to avoid repeated searches for the same non-existent album.

## Legal Disclaimer
This project is for educational purposes only. It uses web scraping techniques to retrieve publicly available metadata. Please respect Apple Music's Terms of Service and use this tool responsibly.
