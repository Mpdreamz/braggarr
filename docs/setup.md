# Setting Braggarr up

## Run it

Braggarr is one container with one volume. `compose.yaml` in this repository is an example: it builds the image from the
clone and keeps everything in `./config`:

```sh
docker compose up -d --build
```

It listens on port 9494. `/healthz` answers `ok`.

## First run

1. Create an **empty private repo** on GitHub for the posts, for example `owner/braggarr-posts`.
2. Braggarr prints a one-time setup link to its log:

   ```sh
   docker logs braggarr 2>&1 | grep setup/start
   ```

   Open that path on the host you will use Braggarr from, for example `http://braggarr-host:9494/setup/start?token=…`. The token works
   until the owner signs in, and is printed again on every start until then.
3. The setup page walks through the rest:
   - name the posts repo;
   - **create Braggarr's GitHub App**: GitHub fills in a form from a manifest (private, no webhooks, read and write on contents,
     pull requests and issues) and you confirm;
   - **install** the app on the posts repo only;
   - **sign in with GitHub**: that account becomes the owner, and from then on the only one that gets in. Braggarr keeps its
     numeric id, which a rename cannot change;
   - add the starter `README.md` and `voice.md` to the posts repo.
4. In **Settings**, connect Bluesky (an app password), Mastodon (an access token with `write:statuses`, `write:media`, `read:accounts` and `write:accounts`), LinkedIn and Threads
   (see below), X (four keys, below), make a key for
   each agent, and add a Claude API key to have variants drafted. Put that key in its own Anthropic workspace with a spend
   limit; Settings → Claude shows what Claude cost this month, by use (from `/config/usage.jsonl`).

The GitHub App's callback URL is the address you used during setup. When Braggarr moves to a public hostname, such as
`braggarr.example.com`, add `https://braggarr.example.com/login/callback` to the app's callback URLs on GitHub.

## LinkedIn and Threads

Both sign in through OAuth with an https return address, which Braggarr over Tailscale does not have, so you paste a token:

- **LinkedIn:** create an app on the LinkedIn developer portal, add the "Sign In with LinkedIn using OpenID Connect" and "Share
  on LinkedIn" products, then use the portal's OAuth token generator with `openid`, `profile` and `w_member_social`. The token
  lasts 60 days and LinkedIn gives self-serve apps no refresh, so Braggarr notifies you daily from a week before it runs out;
  after that LinkedIn posts wait for a new token. LinkedIn has no idempotency key: if it answers a post with a server error,
  the delivery is marked unknown for you to check, never retried blindly.
- **Threads:** in a Meta app with the Threads use case (`threads_basic`, `threads_content_publish`), add yourself as a tester
  and generate a long-lived user token. Braggarr refreshes it when it has less than 30 days left. Threads gets text only.

## X

X takes OAuth 1.0a user context, which needs no sign-in round trip: in X's developer portal, set your app's user
authentication to read and write, then generate the access token and secret for your own account, and paste the four keys.
X bills each post (one with a link costs more), and has no idempotency key: a server error or a lost answer marks the delivery
unknown for you to check.

## YouTube

YouTube needs your own Google Cloud OAuth client, and Google sends you back to Braggarr only over https. So it is connected
through a public https hostname (`https://braggarr.example.com`), not a private address.

1. In Google Cloud, create a project and enable the **YouTube Data API v3**.
2. Configure the OAuth consent screen (external) and set its publishing status to **In production**. Leave it unverified:
   Google shows an "unverified app" warning once, and you click through it. While it is in **Testing**, Google ends
   Braggarr's access after 7 days.
3. Create an OAuth client of type **Web application**, with the redirect URI that Settings → Networks → YouTube shows
   (`https://braggarr.example.com/settings/youtube/callback`).
4. In Braggarr, open Settings → Networks through the public hostname, paste the client id and secret, save, then
   **Connect YouTube** and allow uploading and reading your channel.

What it does:
- A YouTube post is a video, uploaded public at the post's time. The variant's first line is the title (at most 100
  characters), and the rest is the description (at most 5,000 bytes). YouTube refuses `<` and `>` in either.
- YouTube decides by itself whether a vertical video of three minutes or less is a Short.
- Videos are limited to 15 minutes until your Google account is verified.
- An upload costs 1 unit of a 100-a-day quota.
- **When an upload fails:**
  - A dropped upload resumes from what YouTube has.
  - Anything refused before the video exists is safe to retry.
  - Only an answer lost after the last byte, that YouTube itself cannot be asked about, leaves the delivery unknown for
    you to check.
- **Approval:** a campaign cannot be approved while a YouTube post's video is still only wanted.

## A blog

Under Settings → Networks → Blog, connect a site built by braggarr ([the blog](blog.md)): its repository (`owner/blog`), the
branch Braggarr commits to, the folder its posts are in (`posts`), and, when the site is served somewhere else than the `url`
in its `braggarr.yml`, that address. Braggarr's GitHub App must be installed on the site's repository; connecting reads its
`braggarr.yml` to check.

Tick Blog on a campaign, or on a watched project to have its releases and introduction written up there too. When a blog post
is due, Braggarr commits it (`posts/<date>-<slug>/index.md` with its images) in one commit, which builds the site; once its
address answers, the campaign's other posts that link it with `{blog}` go out with the address. If the address does not
answer within an hour, you get a notice and decide. Only public projects get a blog post.

## Watched projects

Under Settings → Watched projects, add a public repository (`nullean/curb`) and the networks to announce it on. Every hour
Braggarr looks at its GitHub releases. The first look only remembers the latest one; after that, a new release (prereleases
only if you ask) becomes a proposed campaign for ten the next morning, written by Claude from the release notes, proposed by
`release-watcher` and waiting for your review like any other. With "Let Claude judge each release" on (the default), Claude
first reads the notes and skips releases that are only dependency bumps, CI changes or small fixes; the project shows the
skipped release and Claude's reason, with a button to propose it anyway. When Claude is not set up, or cannot answer, the
release is proposed: a release is never dropped by an error. A burst of releases between two looks is one campaign, for the
newest.

Adding a project can also propose an **introduction campaign** (on by default): Claude reads the repository and plans a
launch post plus follow-ups that each zoom in on one feature, spread over the next weeks. It is planned in the background and
opens as a proposal when ready, with a notice.

## Notifications

Set an ntfy topic in Settings (long and random on ntfy.sh, where anyone who knows it can read it) and subscribe to it in the ntfy
app. Braggarr sends a notice when:

- a proposal opens (tap: the pull request);
- a Reddit or Hacker News post is due (tap: the prefilled submit form; buttons for each subreddit and for the issue). Press
  **I posted it** on the campaign page, or close the issue, once it is up;
- a post failed three times, or Braggarr lost the answer while posting (tap: the campaign, to say whether it went out).

**Send a test notification** in Settings checks the topic.

## What is where

| Path | What |
|---|---|
| `/config/config.json` | Settings: the posts repo, the owner's GitHub id, the app's id and client id, time zone, ntfy, agent key hashes |
| `/config/secrets.json` | Network credentials and the app's private key and client secret, encrypted with Data Protection |
| `/config/keys/` | The Data Protection keys. Anyone with these and `secrets.json` can read the secrets: back `/config` up as a whole, privately |
| `/config/posts/` | Braggarr's clone of the posts repo. It can always be cloned again |
| `/config/usage.jsonl` | One line per Claude call: tokens and purpose |
| `/config/media/` | Videos, named `sha256-<hash>.mp4` after their bytes. They are too big for git, so this is their only copy: back it up. Nothing here is ever deleted |

Back up `/config`, videos included. Everything else lives in the posts repo.

Uploads through a reverse proxy: a video is up to 100 MB, and nginx, for one, refuses request bodies over its
`client_max_body_size`. If an upload through the proxy fails with 413, set `client_max_body_size 110m;` for Braggarr's host.
Uploads straight to port 9494 do not go through the proxy.

## Security, briefly

- Only the owner's GitHub account signs in; sessions last 14 days, sliding, and end if the owner changes.
- Agents' keys are stored as SHA-256 hashes, shown once, revocable one by one, limited to 60 requests a minute, and can only
  propose.
- **Approving** is merging the pull request, by the owner on GitHub or on the proposal page, or by the judge when the owner
  turned that on. An approval records a fingerprint of what was approved, so any edit afterwards undoes it until it is approved
  again. What the judge approves waits a delay the owner set, and the owner is told, so it can be held.
- **Accounts:** any number per network, each connected on its own under Settings → Networks; Disconnect removes one and the
  defaults that named it. Who posts for what is set in the table below them: for everything, for a GitHub org's projects, and
  for one project. A proposal takes its accounts from there when it is made and shows them; change them on it before you
  approve. Upgrading from one connection per network moves each into an account of its own, the default for everything.
- **Profiles** (Settings → Profiles) reads each connected account's profile and says what a visitor misses: a display name,
  a bio that says what the account posts about, a link, a picture and a banner. Claude suggests a name, bio and link from the
  briefs of the projects the account posts for (by the Who posts defaults). Bluesky, Mastodon and X take the name, bio and
  link from there, YouTube the channel description; LinkedIn and Threads have no API for it, so their text is copied. Nothing
  changes until you apply a field. Mastodon needs the `write:accounts` scope for it, and YouTube asks to manage the channel
  (the `youtube` scope) when connected. Pictures and banners come with the artwork.
- **Rebalance** (Calendar → Rebalance) spreads every post that has not gone out, of proposals and approved campaigns, over
  the days and hours you allow: rest days with no posts, at most so many posts a day per network, and posting hours. A post
  that already keeps them stays; nothing moves earlier, a campaign's posts keep their order and spacing, and a post of
  which anything went out stays put. Preview shows each move first. Applying moves proposals on their branches and
  approved campaigns on main, and approves those again for their new times as you, unless the judge has criteria: then it
  reads the moved campaign first, as after any edit. The options are kept for next time.
- **The judge** (Settings → Judge) reads proposals against the criteria in `judges/` (see [the posts repo](posts-format.md#judges)).
  Its model and effort are under Settings → Claude, next to the key and the writing model, and default to those. A blocking failure stops approval
  on the proposal page; merging on GitHub is the owner's own key and is not stopped. Letting the judge approve is off until the
  owner turns it on, and it approves only what passes every criterion, warnings included.
- **Prompt injection:** a post is text an agent wrote. The judge's instructions and the criteria come from `main`; the campaign
  is sent as escaped data with an instruction that nothing in it is addressed to the judge, and a post that tries to steer its
  judge fails. What the model says about whether a failure blocks is ignored: that is the criterion's.
- Every page sends a strict Content-Security-Policy (no inline script), `X-Frame-Options: DENY`, `nosniff` and
  `Referrer-Policy: same-origin`; every form checks an anti-forgery token.
- The log redacts every secret Braggarr holds.
- Behind a reverse proxy in Docker, Braggarr joins the proxy's network and the proxy forwards to `http://braggarr:9494`.
  Under Settings → Trusted proxies, list that network's subnet (`docker network inspect proxy_default`); it applies
  after a restart. Only requests from it may set the scheme, host and client address; with the list empty no forwarded header
  is read at all (ASP.NET would otherwise believe them from anyone). On Docker Desktop, everything reaching a published port
  arrives from a gateway address, so trust the shared network, never that gateway.
