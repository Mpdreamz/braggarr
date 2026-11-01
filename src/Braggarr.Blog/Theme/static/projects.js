// Braggarr's theme: today's stars and latest release of each project on the page, from GitHub's public API. Without it (no
// JavaScript, GitHub's rate limit, offline) the page keeps what was true when it was built.
(() => {
	const hour = 60 * 60 * 1000;
	const day = new Intl.DateTimeFormat("en-GB", { day: "numeric", month: "long", year: "numeric" });

	// One answer per address per hour, kept in the browser: GitHub allows 60 unauthenticated requests an hour.
	async function ask(url, pick) {
		const key = "braggarr:" + url;
		try {
			const kept = JSON.parse(localStorage.getItem(key));
			if (kept && Date.now() - kept.at < hour) return kept.value;
		} catch { /* storage refused or empty */ }
		const response = await fetch(url, { headers: { Accept: "application/vnd.github+json" } });
		// 404: no release yet; 403 or 429: the rate limit. Either way, keep what the page says.
		if (!response.ok) return null;
		const value = pick(await response.json());
		try { localStorage.setItem(key, JSON.stringify({ at: Date.now(), value })); } catch { /* storage refused */ }
		return value;
	}

	async function refresh(project) {
		const repo = project.dataset.projectRepo;
		const api = "https://api.github.com/repos/" + repo;
		const [stats, release] = await Promise.all([
			ask(api, r => ({ stars: r.stargazers_count })),
			ask(api + "/releases/latest", r => ({ tag: r.tag_name, date: r.published_at, url: r.html_url }))
		]);
		const stars = project.querySelector("[data-stars]");
		if (stars && stats && Number.isInteger(stats.stars)) {
			stars.querySelector("[data-count]").textContent = stats.stars.toLocaleString("en");
			stars.hidden = false;
		}
		const link = project.querySelector("[data-release]");
		if (link && release && release.tag && typeof release.url === "string" && release.url.startsWith("https://github.com/" + repo + "/")) {
			link.href = release.url;
			link.querySelector("[data-tag]").textContent = release.tag;
			const time = link.querySelector("time");
			const date = new Date(release.date);
			if (!Number.isNaN(date.getTime())) {
				time.dateTime = release.date;
				time.textContent = day.format(date);
			}
			link.hidden = false;
		}
	}

	for (const project of document.querySelectorAll("[data-project-repo]")) {
		refresh(project).catch(() => { /* offline: keep what the page says */ });
	}
})();
