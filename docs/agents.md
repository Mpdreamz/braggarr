# Braggarr in a Claude Code session

The `plugin/` folder is a Claude Code plugin, and this repository is its marketplace. It gives a session:

- **Braggarr's MCP tools** (`list_campaigns`, `calendar`, `propose_campaign`, `propose_introduction`, `add_post`,
  `redraft_variant`, `add_image`), connected over HTTP with an agent key;
- **`/brag [what]`**: gathers the facts from the repository and proposes a campaign;
- **the `braggarr` skill**: when a campaign is worth proposing, how to rank talking points and ask for media, and what makes a
  good brief;
- **a hook after `gh release create` or a tag push**: it reminds the session that it can offer you a campaign. It asks you
  first, and proposing never posts anything: you approve every campaign as a pull request.

## Install

```
/plugin install braggarr --marketplace Mpdreamz/braggarr
```

The repository is private, so the session needs read access to it through your GitHub login (`gh auth status`).

## Connect it

The plugin reads two environment variables, so no key is written into the plugin:

- `BRAGGARR_URL`: where Braggarr answers, without a trailing slash: `http://braggarr-host:9494`, or
  `https://braggarr.example.com` behind a proxy;
- `BRAGGARR_API_KEY`: a key made for this under Settings → Keys for agents, named after where it is used (`claude-code`).

Set them where your shell gets its secrets, not in a file you commit or sync in plain text. Without `BRAGGARR_URL` the hook
stays quiet. Revoking the key in Braggarr cuts the plugin off at once.
