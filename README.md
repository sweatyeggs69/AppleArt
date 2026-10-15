# [<img src="AppleArt/wwwroot/assets/logo.png" width="40" alt="" style="vertical-align: middle;">](https://amaa.tromme.app) AppleArt

[![Website](https://img.shields.io/badge/Public_Instance-blue?style=for-the-badge)](https://amaa.tromme.app)
[![Build Status](https://img.shields.io/github/actions/workflow/status/sweatyeggs69/AppleArt/docker-publish.yml?style=for-the-badge)](https://github.com/sweatyeggs69/AppleArt/actions)

<div align="center">

<img src=".github/assets/preview.png">

A lightweight tool to fetch and display Apple Music’s animated album covers (HLS/m3u8). Supports both square and tall cover variants, as well as playlist artworks. Built with .NET 10 and a minimal Tailwind CSS frontend. Powers animated artwork in [Tromme](https://github.com/sweatyeggs69/Tromme).

**Test it out:** [https://amaa.tromme.app](https://amaa.tromme.app)

</div>

## What it does
- Search: Accepts artist and album names, or Apple Music URLs, to find the corresponding animated artwork.
- Scraping: Pulls the .m3u8 stream URLs directly from Apple Music.
- Download: Allows in-browser preview and download of the animated covers.
- Multiple Variants: Supports both the standard square cover and the taller variant used for lock screen & expanded artwork views.
- API: Provides a simple REST API for programmatic access to the artwork data.
- Cache: Saves results in local cache files. It only hits Apple's servers once per album.
- Web Player: Simple UI using hls.js to play the animated covers in any browser (not just Safari).

## API Reference

Base URL: https://amaa.tromme.app

**Get Artwork by Details**

```GET /api/v1/artwork/search?artist=Linkin+Park&album=Living+Things```

**Get Artwork by URL**

```GET /api/v1/artwork/url?url=https://music.apple.com/us/album/...```

**Get Global History**

```GET /api/v1/artwork/history```

## Caching Strategy
This project is designed to be "Apple-friendly" to avoid rate limits:
1. **Edition Matching:** Searches pick the closest album title, so an exact match beats editions such as "Deluxe" or "Anniversary". Cached lookups match the normalized album name exactly.
2. Negative Artwork Caching: Albums that are confirmed to have no animated artwork are cached as NONE to prevent repeated futile scraping.
3. Negative Search Caching: If a search query returns no matches, that result is cached to avoid repeated searches for the same non-existent album.

## Legal Disclaimer
This project is for educational purposes only. It uses web scraping techniques to retrieve publicly available metadata. Please respect Apple Music's Terms of Service and use this tool responsibly.
