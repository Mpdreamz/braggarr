# The blog

`braggarr` is a static site builder for a blog: markdown posts, one built-in theme, an Atom feed. It is the same code Braggarr
uses to write blog posts from campaigns, so what you preview is what Braggarr publishes. It needs nothing installed: it ships
as one native binary.

```sh
braggarr blog init            # braggarr.yml, an about page and a first post
braggarr blog new "Curb 1.0"  # posts/2026-11-02-curb-1-0/index.md, a draft dated now
braggarr blog serve           # http://localhost:3000, drafts shown, reloads when a file changes
braggarr blog build --strict  # .artifacts/site, failing on warnings too
```

`braggarr blog <command> --help` lists every option. The blog commands sit under `blog` because `braggarr` is also the Braggarr server.

## Getting it

- **A binary:** each [release](https://github.com/Mpdreamz/braggarr/releases) has `braggarr-<platform>.zip` for linux-x64,
  linux-arm64, osx-arm64 and win-x64. On Linux and macOS:

  ```sh
  curl -fsSL https://raw.githubusercontent.com/Mpdreamz/braggarr/main/install.sh | sh
  ```

- **The image:** `ghcr.io/mpdreamz/braggarr` (`edge` follows main, `latest` the last release). It holds only the binary,
  on Ubuntu's chiseled base (the variant with ICU and time zones, which the server in the same binary uses), and runs as
  uid 1001:

  ```sh
  docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/site" -w /site ghcr.io/mpdreamz/braggarr:edge blog build --strict
  ```

- **From source:** `dotnet run --project src/Braggarr.Cli -- blog serve --path ../my-blog`.

## In GitHub Actions

The repository's root is an action that builds a site, and `actions/publish` builds it and deploys it to GitHub Pages:

```yaml
# .github/workflows/pages.yml in the blog's repository
on:
  push:
    branches: [main]
  schedule:
    - cron: "17 * * * *"   # hourly, so a post dated later goes out after its date
permissions:
  contents: read
  pages: write
  id-token: write
concurrency:
  group: pages
  cancel-in-progress: false
jobs:
  publish:
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.publish.outputs.page_url }}
    steps:
      - uses: actions/checkout@v4
      - id: publish
        uses: Mpdreamz/braggarr/actions/publish@main
```

In the repository's settings, set Pages to deploy from GitHub Actions. To only build (a pull request check, another host),
use the action itself:

```yaml
      - uses: Mpdreamz/braggarr@main
        with:
          path: .            # the folder with braggarr.yml
          output: .artifacts/site
          strict: true
```

Problems show as annotations on the files in the pull request.

## A site's folder

| Path | What |
|---|---|
| `braggarr.yml` | The site: its title, address, author, and where posts live |
| `posts/<name>/index.md` | A post. Files beside it (images, say) are published next to it, so `![A diff](diff.png)` works as written |
| `posts/<name>.md` | A post with no files of its own |
| `pages/<name>.md` | A page that is not a post: `pages/about.md` is `/about/` |
| `static/` | Copied to the site's root as it is: `static/images/me.jpg` is `/images/me.jpg`. A `static/custom.css` is linked after the theme's |
| `head.html`, `footer.html` | Markup added to every page's head and footer, as it is |
| `_braggarr/profiles.yml` | Written by Braggarr: the owner's profiles, linked from the author card with `rel="me"` |

A name may start with its date, `2026-11-02-curb-1-0`. The date is the post's when its front matter has none, and the rest
is its slug. Hidden folders (`.git`, `.github`), `node_modules`, and anything reached through a link are never read.

## `braggarr.yml`

```yaml
title: My blog
description: What this blog is about, in a sentence.
url: https://example.com/            # the site's root; https://example.com/blog/ works too
author:
  name: Your Name
  avatar: images/avatar.jpg          # a file in static/, or a full https address
  bio: One line about you.
permalink: /posts/{slug}/            # {slug} required; {year}, {month}, {day} allowed
nav:
  - { title: About, page: about }
  - { title: Archive, href: /archive/ }
accent: "#d9480f"                    # the theme's accent colour
comments:                            # giscus: GitHub Discussions; the ids come from https://giscus.app
  giscus: { repo: owner/name, repo_id: R_…, category: Comments, category_id: DIC_… }
feed: feed.xml                       # the Atom feed's path
posts_per_page: 10
feed_size: 20
```

Profile links are not set here: Braggarr writes them from the networks the owner connected. A key the site does not know is
an error, so a typo is not silently ignored.

## Front matter

```yaml
---
title: Curb 1.0 is out
date: 2026-11-02T09:00:00+01:00   # with its offset; without one it is read as UTC, with a warning
description: A C# formatter that keeps diffs small.   # 160 characters at most; the first paragraph when left out
tags: [dotnet, release]           # lowercase letters, digits and dashes
draft: true                       # never published, except by serve and build --drafts
slug: curb-1-0                    # instead of the name's
cover: card.png                   # beside the post, or a site path, or an https address
cover_alt: A diff with one changed line   # required with a cover
aliases: [/2026/curb/]            # old paths: each gets a page that sends readers on
canonical: https://elsewhere.example/curb  # when this is a copy
project: curb                     # written by Braggarr
campaign: curb-1-0                # written by Braggarr
---
```

A post dated later than the build is left out until a build after its date: a scheduled post can wait in the repository.

## What a build writes

- The index, `posts_per_page` at a time (`/`, `/page/2/`, …), each post at its permalink, each page, a page per tag
  (`/tags/<tag>/`), the archive (`/archive/`) and `404.html`.
- The Atom feed with the newest posts and their full text, `sitemap.xml` and `robots.txt`.
- A redirect page at each alias.
- On every page: a canonical link, OpenGraph and Twitter cards, and on posts JSON-LD (`BlogPosting`).
- The theme's stylesheet under `/_theme/`, named by its content.

Building twice gives the same bytes, whatever order the disk lists the posts in. Nothing outside the output folder is written,
and what an earlier build left there that this one did not write is removed.

`--strict` also fails on warnings: a link or image on this site that leads nowhere, an image without alt text, a date
without an offset. Links to other sites are not followed: a build never goes to the network. Write links to other posts as
paths on the site (`/posts/curb-1-0/`) or relative to the post (`../curb-1-0/`).

## Braggarr posts to it

Connect the site to Braggarr (Settings → Networks → Blog) and a campaign can have a blog post: an article Claude writes from
the talking points, which you review with the rest. When it is due, Braggarr commits `posts/<date>-<slug>/index.md` with its
images to the site's branch, which builds the site, and knows the address before it commits: it is this site's permalink
rule. Committing again finds the post there and changes nothing.

## The theme

One theme is compiled in (RazorSlices), which is what lets `braggarr` be a native binary. It cannot be replaced per site;
change it with `accent`, `static/custom.css`, `head.html` and `footer.html`. Pages work without JavaScript; giscus loads
comments where JavaScript runs.
