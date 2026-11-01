---
name: braggarr
description: How to propose a good Braggarr campaign. Use when the owner shipped or released something worth telling people about (a release, a new project, a notable feature), or asks to announce, promote, post about or "brag about" their work.
---

# Proposing to Braggarr

Braggarr is the owner's self-hosted tool that posts about their projects to Bluesky, Mastodon, LinkedIn, Threads and X, and
prepares Reddit and Hacker News posts for them to submit. Its MCP tools let you propose; the owner reviews every proposal as a
pull request and approves it. Nothing you do posts anything.

## When to propose

- A release with something a user would notice: a feature, a speed-up, a long-awaited fix, a first or major version.
- A new project that has never been announced: use `propose_introduction`, which ranks its talking points from the README.
- An article for the owner's blog, about something that deserves more than a post: use `propose_blog_post` (see below).
- Not for dependency bumps, CI changes, refactors or small fixes. When unsure, ask the owner rather than propose.

## Talking points first

Give `propose_campaign` talking points rather than posts. Braggarr lays the posts out from them: on Bluesky, Mastodon,
Threads and X every point gets a short post of its own, two days apart; LinkedIn and Reddit get one longer post with the top
points; Hacker News a title for the first; YouTube a video for each point that wants one. Claude then writes each post to its
points.

- Rank them, most important first. The first is the headline: what it is, or what is new.
- One sentence each, one idea each. Two to six points is plenty; a point that would be filler is better left out.
- Say what media would show a point, as `wanted`: `{ "kind": "image" | "video", "description": "…" }`. Describe it so
  the owner can make it: "a terminal recording of curb formatting a file where one line changes", not "a demo". A
  point that is better without media wants none. The owner fills these slots before approving; one left empty goes out
  without it.
- `start` sets when the first post goes out (ISO 8601 with offset); left out, it is ten the next morning.

Only pass `posts` when you need to control them yourself. With neither points nor posts, Claude finds the points in the brief.

## A good brief

The brief is what the points and the posts are written from. It is facts, not a post:

- What it is, in one sentence, for someone who has never heard of it.
- What is new, and why it matters to the person using it.
- One concrete detail worth leading with: a number from the release notes, a before and after, a command.
- How to get it: the install line, the link.
- Nothing invented: no features, numbers or quotes that are not in the repository.

## Blog posts

Braggarr can post to the owner's blog, for public projects only: it asks GitHub and refuses a private one. Never write about a
project that is not public.

- In a campaign, add `blog` to the networks for a launch or a major version: Claude writes one article with every point, it
  goes up first, and the other posts may link it as `{blog}`, which becomes its address once it is live.
- On its own, `propose_blog_post` takes an article you write: markdown, a title (at most 100 characters), a description (at
  most 160) and lowercase tags. `announce` names networks for one post linking it, half an hour after it goes up.
- An article is longer and calmer than a post: what it is, the problem, how it works with a real example, how to get it.
  Facts from the repository only, as with a brief. Images are `media/name.png`, added afterwards with `add_image`.

## Times and networks

- Check `calendar` first, and pick a `start` that does not crowd other campaigns; prefer mornings in the owner's time zone
  (Europe/Amsterdam).
- Hacker News and Reddit suit launches and major versions, not every release.

## After proposing

Tell the owner the pull request number and what it proposes. They may ask to change one variant: use `redraft_variant` with a
note. Images go up with `add_image` (PNG, JPEG, WebP or GIF, at most 1 MB); one fills the post's first wanted image slot.
Videos (MP4, at most 100 MB) go up with a multipart `POST /api/v1/campaigns/{slug}/media` and fill the wanted video slot;
the owner can also upload them on the proposal page.
