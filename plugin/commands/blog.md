---
description: Propose a post for the owner's blog about a public project
argument-hint: "[what to write about, or a repository like owner/name]"
---

Propose a blog post to Braggarr about: $ARGUMENTS

If nothing was given, work it out from this session and the repository: the latest release, or what was just finished.

1. Check that the project is public (`gh repo view --json visibility`). Braggarr refuses a blog post for a private project;
   never write about one.
2. Gather the facts from the repository and not from memory: the README, the release notes, the code that shows the point.
3. Write the article in markdown: what it is, the problem it solves, how it works with a real example, and how to get it. Give
   it a title of at most 100 characters, a description of at most 160, and a few lowercase tags.
4. Check the `calendar` tool and pick a morning that does not crowd other posts.
5. Use `propose_blog_post` with the project, title, description, markdown, tags and time. Add `announce` with networks for a
   post linking it, if it deserves one.
6. Report the pull request it opened. The owner reviews and approves; never claim it is published.

Use the braggarr skill for what Braggarr expects.
