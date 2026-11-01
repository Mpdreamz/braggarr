// Progressive behaviour only: every page works without this file.

// "Copy" buttons: data-copy holds a selector for the element whose text is copied.
for (const button of document.querySelectorAll("[data-copy]")) {
  button.addEventListener("click", async () => {
    const source = document.querySelector(button.dataset.copy);
    if (!source || !navigator.clipboard) return;
    await navigator.clipboard.writeText(("value" in source ? source.value : source.textContent).trim());
    const label = button.textContent;
    button.textContent = "Copied";
    setTimeout(() => (button.textContent = label), 1500);
  });
}

// Times are shown in the owner's time zone by the server; a title gives the full timestamp on hover.
for (const time of document.querySelectorAll("time[datetime]")) {
  time.title = new Date(time.dateTime).toString();
}

// Live character counts next to the editor's text boxes. An estimate in the browser, close to each network's own count; the
// server checks the real one when saving.
const segmenter = "Segmenter" in Intl ? new Intl.Segmenter(undefined, { granularity: "grapheme" }) : null;
const graphemes = (text) => (segmenter ? [...segmenter.segment(text)].length : [...text].length);
const urls = /https?:\/\/\S+/g;
const withUrls = (text, count) => count(text.replace(urls, "")) + (text.match(urls) ?? []).length * 23;
const firstLine = (text) => text.trimStart().split("\n")[0].trim();
const measure = {
  bluesky: graphemes,
  mastodon: (t) => withUrls(t, (s) => [...s].length),
  linkedin: (t) => [...t].length,
  threads: (t) => [...t].reduce((n, c) => n + (c.codePointAt(0) >= 0x1f000 ? 4 : 1), 0),
  x: (t) => withUrls(t, (s) => [...s].reduce((n, c) => n + (c.codePointAt(0) <= 4351 ? 1 : 2), 0)),
  reddit: (t) => graphemes(firstLine(t)),
  hackernews: (t) => graphemes(firstLine(t)),
};
for (const box of document.querySelectorAll("textarea[data-network]")) {
  const label = document.querySelector(`[data-count-for="${box.id}"]`);
  const count = measure[box.dataset.network];
  if (!label || !count) continue;
  const update = () => {
    const n = count(box.value);
    label.textContent = `${n} / ${box.dataset.limit}`;
    label.classList.toggle("over", n > Number(box.dataset.limit));
  };
  box.addEventListener("input", update);
}
