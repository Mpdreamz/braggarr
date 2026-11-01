# The API for agents

Base: `/api/v1`. Every request sends a key, made under Settings, in the `X-Api-Key` header. A key can propose campaigns and read
what is planned; nothing in the API approves, merges or publishes. Each key may make 60 requests a minute.

The OpenAPI document is at `/openapi/v1.json` (with a key).

## Propose a campaign

Send talking points, not posts. Braggarr lays the posts out from them and Claude writes each one:

```http
POST /api/v1/campaigns
X-Api-Key: brag_…
Content-Type: application/json

{
  "title": "Curb 1.0",
  "project": "curb",
  "networks": ["bluesky", "mastodon", "linkedin", "hackernews"],
  "links": ["https://github.com/nullean/curb"],
  "brief": "Curb 1.0 formats C# without moving lines, so a diff shows only what changed. dotnet tool install -g curb.",
  "start": "2026-11-02T10:00:00+01:00",
  "points": [
    { "text": "Curb 1.0 is out: a C# formatter that keeps your diffs small",
      "wanted": [{ "kind": "video", "description": "A terminal recording of curb formatting a file where one line changes" }] },
    { "text": "It runs as a dotnet tool and as a GitHub Action",
      "wanted": [{ "kind": "image", "description": "The Action's check on a pull request" }] },
    { "text": "It formats a 100k-line solution in under a second" }
  ]
}
```

- **`points`**, most important first, one sentence each. **`wanted`** says what media would show a point: `image` or
  `video`, and a description the owner can make it from. The owner fills those slots before approving (on the proposal page,
  or with `POST …/media`); a slot still empty when its post is due goes out without it.
- **The layout:**
  - Bluesky, Mastodon, Threads and X get one short post per point, two days apart, from **`start`** (ten the next
    morning in the owner's time zone when left out).
  - LinkedIn and Reddit get one longer post with the top five points, on the start day.
  - Hacker News gets a title for the first point.
  - YouTube gets one video post for each point whose `wanted` has a video. The campaign cannot be approved until each of those
    videos is uploaded.
  - Every point reaches every short network; a proposal that does not cover them cannot be approved.
- **With a brief and no points,** Claude finds the points in the brief. Without Claude, the title is the one point.
- **`posts`** are for when you want to control the posts yourself. Each has an `at`, optionally `networks` (some of the
  campaign's), `points` (the ones it covers, from 1) and `variants` (text per network). Claude writes the variants left out,
  following `voice.md` and each network's limit. A variant that comes back too long is asked for again, up to three times.
  Text that was sent is never touched.

**`accounts`** (optional) names the account to post as per network, by the name Settings shows for it:
`{ "bluesky": "nullean.bsky.social" }`. A network left out gets the owner's default for the project. Naming an account that is
not connected is a `400` that lists the ones that are. A `PATCH` keeps the proposal's accounts unless it names others. The
owner can change them on the proposal page; the response carries them as `accounts`.

`repeatMonths` (1 to 24) proposes it again, written fresh, that long after its last post.

`201 Created` with the campaign, its `pullRequest` number and `pullRequestUrl`. The slug comes from the title unless `slug` is
given. Each post in it carries its `networks`, `points` and `angle`; wanted media show as `"wanted": true` with the description as
`alt`. A `400` lists every problem: a network Braggarr does not know, a media kind that is not image or video, a slug that is taken, two
posts with one id.

A proposal may be incomplete (a missing variant, one that is too long): the owner sees what is missing and cannot approve it
until it is fixed. Each variant in the response carries its `length`, `limit` and `fits`.

## Change a proposal

- `PATCH /api/v1/campaigns/{slug}` with the same body replaces the proposal (a new commit on its branch).
- `POST /api/v1/campaigns/{slug}/posts` with one post (`id`, `at`, `variants`) adds it.
- `POST /api/v1/campaigns/{slug}/posts/{post}/redraft` with `{ "network": "x", "note": "shorter, mention the CLI" }` has Claude
  write that one variant again.

- `POST /api/v1/campaigns/{slug}/media`, multipart, adds an image to one post: `file` (PNG, JPEG, WebP or GIF, at most 1 MB,
  Bluesky's limit), `post` (its id), and optionally `networks` (comma separated; every network of the post that takes images
  when left out) and `alt`. It fills the post's first wanted image slot. Without `alt`, Claude looks at the image and writes
  it; without Claude, `alt` is required. Up to four images per variant; Reddit and Hacker News posts take none.
- The same endpoint takes an MP4 video of up to 100 MB. It is kept on the server, not in git, and fills the post's wanted
  video slot on Bluesky, Mastodon, LinkedIn and X; without `alt`, the slot's description is used. A post carries one video or
  images, never both, and a video longer than a network takes is refused with the network named. Videos are not sent
  through MCP: base64 of that size does not belong in a tool call.

Only open proposals change through the API. Once merged, a campaign is the owner's.

## Propose a blog post

One article about a public project, on the owner's blog. It becomes a small campaign of its own, `blog-<slug>`, that the
owner reviews like any other:

```http
POST /api/v1/blog
X-Api-Key: brag_…
Content-Type: application/json

{
  "project": "curb",
  "title": "Curb 1.0, from the start",
  "description": "What curb does to a diff, and why it stays small.",
  "markdown": "# Curb 1.0\n\nCurb formats C# so a diff shows only what changed…",
  "tags": ["dotnet", "formatting"],
  "at": "2026-11-05T10:00:00+01:00",
  "announce": ["bluesky", "mastodon"]
}
```

- `title` is at most 100 characters, `description` at most 160 (feeds and previews show it), `tags` are lowercase slugs.
- `at` is when it goes up; ten the next morning in the owner's time zone when left out. `slug` sets its address; it is made
  from the title when left out.
- `announce` adds one post on those networks, half an hour after `at`, that links the article. Claude writes it, and it waits
  until the article is live.
- Images are `media/name.png` in the markdown, added with `POST /api/v1/campaigns/blog-<slug>/media` like any other.
- Only a public project gets a blog post: Braggarr asks GitHub, and refuses one for a private project or one it cannot find.

A campaign can also include `blog` in its `networks`: Claude then writes the article from the talking points, and the other
posts may link it as `{blog}`. A blog variant in a response carries an `article` (title, description, tags, slug, cover) and
`limit` 0, since an article has no length limit.

## Introduce a project

`POST /api/v1/projects/introduce` with `{ "repository": "nullean/curb", "name": "Curb", "networks": ["bluesky", "x"] }`
answers `202 Accepted` at once. In the background Claude reads the repository (description, README, recent releases) and ranks
its talking points: what it is first, then the features that deserve a post of their own, each with the media it wants. The
posts are laid out from them as above, drafted, and opened as a proposal from `introducer`. The MCP tool is `propose_introduction`.

## Read

- `GET /api/v1/campaigns`: proposals and approved campaigns, with each variant's delivery state and link.
- `GET /api/v1/campaigns/{slug}`: with the judge's verdict, when there is one, as `judgement`: `ofThisVersion`, `blocked`,
  `allPassed`, and the findings with their reasons. A blocked proposal is approved only after its posts change; fix what the
  reasons say with `PATCH` and the judge reads it again within a minute.
- `GET /api/v1/calendar?from=…&to=…`: posts already planned, so new ones can be spread out.

## MCP

The same powers as MCP tools, at `/mcp` (streamable HTTP, stateless), with the same key header and rate limit:
`list_campaigns`, `get_campaign`, `calendar`, `propose_campaign`, `propose_blog_post`, `add_post`, `redraft_variant` and
`add_image` (base64).
None approves, merges or publishes.

To give a Claude Code session the tools, with a key made for it in Settings:

```sh
claude mcp add --transport http braggarr https://braggarr.example.com/mcp --header "X-Api-Key: brag_…"
```
