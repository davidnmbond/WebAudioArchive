// ClipPlayer behaviour: play/pause, skip, speed, seek bar and the UTC wall time of the current position.
// The wall time comes from the timeline in data-timeline: [{ at: seconds into the clip, utc: epoch ms }, ...].

function pad(n) { return String(n).padStart(2, "0"); }

function clock(seconds) {
    const s = Math.max(0, Math.floor(seconds || 0));
    return Math.floor(s / 3600) + ":" + pad(Math.floor(s / 60) % 60) + ":" + pad(s % 60);
}

function utcText(ms) {
    const d = new Date(ms);
    return d.getUTCFullYear() + "-" + pad(d.getUTCMonth() + 1) + "-" + pad(d.getUTCDate()) + " " +
        pad(d.getUTCHours()) + ":" + pad(d.getUTCMinutes()) + ":" + pad(d.getUTCSeconds());
}

function wallAt(root, t) {
    let timeline = [];
    try { timeline = JSON.parse(root.dataset.timeline || "[]"); } catch { timeline = []; }
    if (timeline.length === 0) return null;
    let p = timeline[0];
    for (const entry of timeline) { if (entry.at <= t) p = entry; }
    return p.utc + (t - p.at) * 1000;
}

export function init(root) {
    if (!root) return;
    const audio = root.querySelector("[data-role=audio]");
    const seek = root.querySelector("[data-role=seek]");
    const position = root.querySelector("[data-role=position]");
    const duration = root.querySelector("[data-role=duration]");
    const wall = root.querySelector("[data-role=wall]");
    const toggle = root.querySelector("[data-action=toggle]");

    const update = () => {
        const d = audio.duration;
        if (isFinite(d) && d > 0) {
            duration.textContent = clock(d);
            if (root.dataset.seeking !== "1") seek.value = String(Math.round((audio.currentTime / d) * 1000));
        }
        position.textContent = clock(audio.currentTime);
        const ms = wallAt(root, audio.currentTime);
        if (ms !== null) wall.textContent = utcText(ms);
        toggle.textContent = audio.paused ? "Play" : "Pause";
    };

    if (root.dataset.bound === "1") {
        // A new clip in the same player: the src attribute has changed, so load it afresh.
        audio.load();
        update();
        return;
    }
    root.dataset.bound = "1";

    audio.addEventListener("timeupdate", update);
    audio.addEventListener("loadedmetadata", update);
    audio.addEventListener("play", update);
    audio.addEventListener("pause", update);
    audio.addEventListener("ratechange", () => {
        root.querySelectorAll("[data-speed]").forEach(b =>
            b.classList.toggle("active", Number(b.dataset.speed) === audio.playbackRate));
    });

    root.addEventListener("click", e => {
        const target = e.target.closest("button");
        if (!target || !root.contains(target)) return;
        if (target.dataset.action === "toggle") {
            if (audio.paused) audio.play(); else audio.pause();
        } else if (target.dataset.action === "back") {
            audio.currentTime = Math.max(0, audio.currentTime - 15);
        } else if (target.dataset.action === "forward") {
            audio.currentTime = Math.min(audio.duration || 0, audio.currentTime + 30);
        } else if (target.dataset.speed) {
            audio.preservesPitch = true;
            audio.playbackRate = Number(target.dataset.speed);
        }
    });

    seek.addEventListener("input", () => {
        root.dataset.seeking = "1";
        const d = audio.duration;
        if (isFinite(d) && d > 0) {
            const t = (Number(seek.value) / 1000) * d;
            position.textContent = clock(t);
            const ms = wallAt(root, t);
            if (ms !== null) wall.textContent = utcText(ms);
        }
    });
    seek.addEventListener("change", () => {
        const d = audio.duration;
        if (isFinite(d) && d > 0) audio.currentTime = (Number(seek.value) / 1000) * d;
        root.dataset.seeking = "0";
    });

    update();
}
