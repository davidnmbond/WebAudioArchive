// Keeps where a calendar's week grid is scrolled to, so that it does not jump when the page is shown again.
//
// A PDCalendar week grid (.pdcal-week-scroll) inside an element with data-keep-scroll="<name>" has its scrollTop kept
// in sessionStorage (per tab, per page path and name) and put back the moment a grid appears: when the prerendered page
// is parsed, when the circuit's first render replaces it, when the Week view is chosen again, and after Back from a
// show. It is put back in the animation frame after the grid appears, before that frame is painted, so no frame is drawn
// at the top first. With nothing kept (or on a host with data-scroll-hour and no data-keep-scroll, which is never kept),
// data-scroll-hour="<hour>" starts the grid at that hour row. That is what PDCalendar's ScrollToHour does, but it
// scrolls from OnAfterRenderAsync, after a frame at the top has been painted.
//
// It also offers waaCalendar.replaceUrl(url), which records the shown week and view in the address without a Blazor
// navigation (no re-render, no focus move), so Back from a show returns to that week. Runs without Blazor too.
(function () {
    "use strict";
    var prefix = "waa-scroll:";
    var pending = new WeakMap(); // grid -> scrollTop still to be put back
    var settled = new WeakSet(); // grids that are where they should be, or that the user has scrolled since

    function hostOf(el) { return el.closest ? el.closest("[data-keep-scroll], [data-scroll-hour]") : null; }
    // Only a data-keep-scroll host has its position kept; a data-scroll-hour host alone always starts at that hour.
    function keyOf(host) {
        var name = host.getAttribute("data-keep-scroll");
        return name === null ? null : prefix + location.pathname + "#" + name;
    }
    function read(key) {
        if (key === null) return null;
        try { var v = sessionStorage.getItem(key); return v === null || isNaN(Number(v)) ? null : Number(v); } catch (e) { return null; }
    }
    function write(key, value) {
        if (key === null) return;
        try { sessionStorage.setItem(key, String(Math.round(value))); } catch (e) { }
    }

    // The offset of the first hour row at or after data-scroll-hour, as PDCalendar computes it for ScrollToHour.
    function hourOffset(host, grid) {
        var hour = Number(host.getAttribute("data-scroll-hour"));
        if (host.getAttribute("data-scroll-hour") === null || isNaN(hour)) return null;
        var rows = grid.querySelectorAll(".pdcal-week-body > .pdcal-week-row");
        for (var i = 0; i < rows.length; i++) {
            var label = rows[i].querySelector(".pdcal-hour-label");
            if (label && parseInt(label.textContent, 10) >= hour) return rows[i].getBoundingClientRect().top - rows[0].getBoundingClientRect().top;
        }
        // The row is not there (yet, while the page is parsed); once it is all there, the last row, as PDCalendar does.
        return document.readyState === "loading" || rows.length === 0 ? null : rows[rows.length - 1].getBoundingClientRect().top - rows[0].getBoundingClientRect().top;
    }

    function place(grid) {
        if (settled.has(grid)) return;
        var host = hostOf(grid);
        if (!host) { settled.add(grid); return; }
        var want = pending.get(grid);
        if (want === undefined) {
            want = read(keyOf(host));
            if (want === null) want = hourOffset(host, grid);
            if (want === null) {
                // Nothing kept and no starting hour: the grid starts at the top. An hour row may not be parsed yet.
                if (host.getAttribute("data-scroll-hour") === null || document.readyState !== "loading") settled.add(grid);
                return;
            }
            pending.set(grid, want);
        }
        grid.scrollTop = want;
        // While the prerendered page is still being parsed the grid may not be tall enough yet; try again next frame.
        if (Math.abs(grid.scrollTop - want) <= 1 || document.readyState === "complete") {
            settled.add(grid);
            pending.delete(grid);
        }
    }

    // Placing runs in an animation frame callback: that is after the stylesheets have loaded (nothing is laid out for
    // painting before), so the rows have their real height, and before that frame is painted, so no frame shows the
    // grid anywhere else. A grid that is not placed yet is tried again each frame until the page has loaded.
    var scheduled = false;
    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(run);
    }
    function run() {
        scheduled = false;
        var grids = document.querySelectorAll("[data-keep-scroll] .pdcal-week-scroll, [data-scroll-hour] .pdcal-week-scroll");
        var unplaced = false;
        for (var i = 0; i < grids.length; i++) {
            place(grids[i]);
            if (!settled.has(grids[i])) unplaced = true;
        }
        if (unplaced) schedule();
    }

    // Keep what the user scrolls to. A grid that is still being put back ignores its own scroll events.
    document.addEventListener("scroll", function (e) {
        var grid = e.target;
        if (!grid || !grid.classList || !grid.classList.contains("pdcal-week-scroll") || !settled.has(grid)) return;
        var host = hostOf(grid);
        if (host) write(keyOf(host), grid.scrollTop);
    }, true);

    // The user taking hold of a grid ends any putting back.
    ["wheel", "touchstart", "pointerdown", "keydown"].forEach(function (type) {
        document.addEventListener(type, function (e) {
            var grid = e.target && e.target.closest ? e.target.closest(".pdcal-week-scroll") : null;
            if (grid) { settled.add(grid); pending.delete(grid); }
        }, { capture: true, passive: true });
    });

    new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });

    window.waaCalendar = {
        // Replaces the address (keeping Blazor's history state) without a navigation.
        replaceUrl: function (url) {
            try { history.replaceState(history.state, "", url); } catch (e) { }
        }
    };
})();
