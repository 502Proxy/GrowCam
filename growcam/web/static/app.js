// The only JavaScript in the app – everything works without it, this just
// gives feedback on slow actions and keeps the live image fresh.

// Buttons with data-busy show a waiting text while the request runs.
document.addEventListener("submit", (event) => {
  const button = event.submitter;
  if (button && button.dataset.busy) {
    // Defer so the button's own value is still submitted.
    setTimeout(() => {
      button.disabled = true;
      button.textContent = button.dataset.busy;
    }, 0);
  }
});

// Live view: reload the camera frame every few seconds while the page is visible.
const live = document.querySelector("img[data-live]");
if (live) {
  const base = live.dataset.live;
  const refresh = () => {
    if (!document.hidden) live.src = `${base}?t=${Date.now()}`;
  };
  live.addEventListener("error", () => live.closest("figure")?.classList.add("offline"));
  setInterval(refresh, 4000);
}
