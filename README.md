# Braggarr

A self-hosted *arr that promotes what you build. You (or an agent) propose a **campaign**: one push for one thing, with posts
written for each network. You review it as a pull request, merge it, and Braggarr posts it on schedule to Bluesky, Mastodon,
LinkedIn, Threads and X. Reddit and Hacker News stay in your hands: Braggarr opens an issue with the text and a submit link
when it is time.

There is no database. The campaigns and the delivery log live in a git repository on GitHub, and `/config` holds only
credentials and settings.

## Docs

- [Setting it up](docs/setup.md)
- [The posts repo's format](docs/posts-format.md)
- [The API for agents](docs/api.md)
- [Braggarr in a Claude Code session](docs/agents.md): the plugin
- [The blog](docs/blog.md): `braggarr`, the static site builder

```
/plugin install braggarr --marketplace Mpdreamz/braggarr
```

Status: milestones 1 to 6: Bluesky, Mastodon, LinkedIn, Threads, X, manual Reddit and Hacker News, the agent API, review as
pull requests, Claude drafting the variants, images with alt text, and notices.

## License

MIT: see [LICENSE](LICENSE).
