# The posts repo

```
README.md
voice.md                          how posts should sound (read by drafting, from milestone 2)
judges/<name>.md                  one criterion each, for the judge (read from main only)
projects/<slug>.md                each project's brief: what drafting writes from and the judge reads
campaigns/<slug>/campaign.md      front matter and the brief
campaigns/<slug>/01-launch.md     one file per post: its time, and a section per network
campaigns/<slug>/00-blog.blog.md  a post's blog article, when the post goes to the blog
campaigns/<slug>/media/…          images, referenced from posts as ![alt](media/name.png)
campaigns/<slug>/deliveries.yml   written by Braggarr only
campaigns/<slug>/judgement.yml    the judge's verdict, written by Braggarr only
```

A campaign on `main` is approved. A campaign on a branch `campaign/<slug>` with an open pull request is proposed; merging the
pull request approves it, closing it rejects it.

## campaign.md

```markdown
---
title: Curb 1.0
project: curb
networks:
- bluesky
- mastodon
- hackernews
accounts:
  bluesky: nullean.bsky.social
  mastodon: hachyderm.io
links:
- https://github.com/nullean/curb
created_by: agent:release-bot
points:
- text: Curb 1.0 is out, a C# formatter that keeps your diffs small
  wanted:
  - kind: video
    description: A terminal recording of curb formatting a file where one line changes
- text: It runs as a dotnet tool and as a GitHub Action
---

The brief: what the campaign is about, for whoever writes or reviews the posts.
```

`points` are the campaign's talking points, most important first. Each post names the ones it covers. `wanted` is media a
point asks for: `kind` is `image` or `video`, with what it should show. A campaign proposed with points and no posts gets its
posts laid out from them:
- On the short networks (Bluesky, Mastodon, Threads, X), every point gets a post of its own, two days apart.
- LinkedIn and Reddit get one post with the top five points, or every point when there is no short network.
- Hacker News gets a title for the first point.
- YouTube gets a video for each point that wants one, alongside that point's short posts.

An approved campaign must reach every short network with every point.

`accounts` names which of the owner's accounts posts on each network, by the name Settings shows for it (a Bluesky handle, a
Mastodon server, a LinkedIn name, a Threads or X username, a YouTube channel). Braggarr writes it when the campaign is proposed,
from the defaults under Settings → Networks → Who posts: the project's, else its GitHub org's, else the one for everything.
It is part of what is approved, so changing it undoes an approval. A network left out posts as today's default, which is
how campaigns from before accounts keep going out as they did. Manual networks (Reddit, Hacker News) have none.

`repeat_months: 3` proposes the campaign again, written fresh, three months after its last post went out (1 to 24; leave
it out for never). Braggarr writes `last_repeat:` when it does, and counts the next repeat from there, so a rejected repeat is
not proposed again until the next interval. The copy is a new campaign that does not repeat itself.

`approved_by`, `approved_digest`, `approved_at` and `approved_not_before` record an approval, and Braggarr writes them.
- **When they are written:** when the owner (or the judge) approves on the proposal page, or, for a campaign merged on GitHub,
  the first time Braggarr sees it on main.
- **The digest:** a fingerprint of everything that goes out (networks, accounts, links, points, and each post's time, networks
  and variants with their media), not the title or the brief.
- **An edit undoes the approval:** any change to those afterwards, by anyone, means the campaign needs approving again (the
  campaign's page has a button for it). What already went out stays out.
- **`approved_not_before`:** set when the judge approved. Nothing goes out before it.

`held: true` in the front matter stops everything that has not gone out yet. Braggarr adds and removes that line itself when you
press Hold or Resume, touching nothing else.

## A post

```markdown
---
at: 2026-11-02T09:30:00+01:00
networks:
- bluesky
- hackernews
points:
- 1
---

## bluesky

Curb 1.0 is out: a C# formatter that keeps your diffs small.
https://github.com/nullean/curb

![A terminal recording of curb formatting a file where one line changes](wanted:video)

## hackernews

Show HN: Curb, a C# formatter that keeps diffs small
https://github.com/nullean/curb
```

`angle:` in a post's front matter (optional) says what that post is about when a campaign spreads over several; drafting
writes to it. `networks:` (optional) sends the post to some of the campaign's networks only; left out, it goes to all of
them. `points:` lists the talking points it covers, counted from 1.

`![what it should show](wanted:image)` (or `wanted:video`) is a slot a point asked for and nobody has filled yet. Replace it
with the real file before approving. One still there when the post is due goes out without it.

A video is `![what it shows](video:sha256-<hash>.mp4)`: an MP4 kept on the server under `/config/media`, named after its
bytes, never in git. Upload it on the proposal page or through the API; Braggarr writes the line. A variant carries one
video or up to four images, never both. Bluesky, Mastodon, LinkedIn and X take video: Bluesky up to three minutes, LinkedIn three
seconds to thirty minutes, X twenty minutes, Mastodon whatever its server allows. Braggarr takes videos up to 100 MB. The file name, without `.md`, is the post's id. An image line under a network's section attaches it to that variant; its alt
text is required, it must live in the campaign's `media/` folder, and a variant takes at most four (none on Reddit and Hacker
News, nor on Threads, which only fetches images from a public URL). Bluesky refuses images over 1 MB; LinkedIn takes PNG, JPEG
and GIF only. Network names: `bluesky`, `mastodon`, `linkedin`, `threads`, `x`, `youtube`, `reddit`,
`hackernews`.

A `## youtube` variant is a video. Its first line is the title (at most 100 characters), and the rest, after a blank line, is
the description (at most 5,000 bytes). Neither may contain `<` or `>`. It carries exactly one video, never an image, and the
campaign cannot be approved until that video is uploaded rather than wanted.

For Reddit and Hacker News the first line is the title. On Reddit, lines such as `r/dotnet` name the subreddits; the issue
Braggarr opens has a submit link for each. The link submitted is the first URL in the text, or else the campaign's first link.

A campaign is scheduled only when every post has a variant for every one of the post's networks and every variant fits:

| Network | Limit | Counted as |
|---|---|---|
| Bluesky | 300 | graphemes (an emoji family is one) |
| Mastodon | 500 | characters, every URL as 23 |
| LinkedIn | 3000 | characters |
| Threads | 500 | characters, an emoji as its UTF-8 bytes (so 4) |
| X | 280 | X's weighting: CJK and emoji count 2, every URL 23 |
| Reddit | 300 | the title |
| Hacker News | 80 | the title |

## A blog article

A post that goes to the blog has its article in a file of its own beside it, `<post>.blog.md`, because an article's markdown
has headings of its own, which would read as the post's `## network` sections. A `## blog` heading in a post's file is text.

```markdown
---
title: Curb 1.0, from the start
description: What curb does to a diff, and why it stays small.
tags:
  - dotnet
cover: media/card.png
cover_alt: Curb's logo
slug: curb-1-0
---

# Why a formatter that moves nothing

…the article, in markdown, with images as ![alt](media/diff.png)…
```

- `title` (at most 100 characters) and `description` (1 to 160) are required; `tags` are lowercase slugs.
- `slug` makes the address (from the title when left out); `cover` and the images in the text come from the campaign's
  `media/` and are copied beside the post on the site.
- Every other post of the campaign may link the article by writing `{blog}` where its address goes. Such a post is planned
  at least half an hour after the blog post and goes out only once the article answers at its address; the address replaces
  `{blog}` then. On the networks that count a link as 23, `{blog}` counts as 23; elsewhere as 60 characters.
- A campaign has at most one blog post, and only a public project gets one: Braggarr asks GitHub when it is proposed,
  approved and posted.

## deliveries.yml

One entry per post and network, with its state (`pending`, `sending`, `published`, `failed`, `unknown`, `manual_due`,
`manual_done`, `committed`), attempts, the link to the post, and the issue for manual networks. Braggarr commits `sending`
before it calls the network, so after a crash it knows to ask instead of posting twice. A blog post is `committed` once it is
in its site's repository, and `published` once its address answers; one that never answers within an hour is `unknown`, for
you to look at. Do not edit it.

## judges/

One markdown file per thing the owner wants every post checked for, in their own words. Once there is one and a Claude key,
the judge reads every complete proposal, and every campaign edited on main since it was approved, against each criterion, for
every post on every network the criterion is about.

```markdown
---
name: honest              # optional; the file name without .md otherwise
on_failure: warn          # block (the default) or warn
networks: [x, bluesky]    # optional; every network otherwise
---

Claims nothing the links do not show: no speed-ups, user counts or comparisons without a source in the post.
```

- **Block:** a failure stops approval, by the owner on the proposal page too, until the post changes and the judge reads it
  again.
- **Warn:** a failure is shown and told; the owner may approve over it, the judge never does.
- The judge reads `judges/` from `main` only, never from a proposal's branch, so a proposal cannot bring its own criteria.
  Files starting with `.` and files not ending in `.md` are left out. A file that cannot be read, has no text, or shares a name
  with another stops approval until it is fixed: nothing is judged by less than the owner wrote.
- Changing a criterion means everything not yet approved is judged again.

## judgement.yml

The judge's verdict on one version of the campaign against one version of the criteria: both fingerprints, the model, the time,
and one finding per criterion, post and network (`passed`, `blocks`, and the reason). It is not part of what goes out, so writing
it does not undo an approval, and a verdict on an earlier version is shown as such and ignored. On a proposal it is committed to
the branch, on `main` next to the campaign. Do not edit it.

## projects/

One file per project: its brief, all in front matter, kept by the owner on the project's page (Projects → the project) or edited by hand. The file is named after the project in lowercase, anything but letters and digits a dash
(`Nullean.Entities` is `projects/nullean-entities.md`).

```markdown
---
project: Curb
tldr: A C# formatter that keeps diffs small.            # one line, at most 160 characters
apart: It never moves a line it does not have to.        # what sets it apart
audience: C# teams who review diffs                      # optional
avoid:                                                   # optional: what a post must never say
  - Calling it a linter
links:                                                   # optional: the links posts use
  - label: Install
    url: https://www.nuget.org/packages/curb
---
```

Drafting (variants and talking points) and the judge get the brief of a campaign's project as the owner's own words, so
only the owner keeps one: the API has no way to write it. "Fill the brief with Claude" on the project's page reads a watched project's
repository (README, description, topics, recent releases) and fills in the form; nothing is kept until the owner saves it,
and a link that is not in the repository's own text is left out of the suggestion.
