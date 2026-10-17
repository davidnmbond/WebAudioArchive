// The show page's transcript: waaShow.reveal(selector) scrolls the matching line into view inside its pane, without
// moving the page itself. Runs without Blazor too.
(function () {
    "use strict";
    function reveal(selector) {
        var el = document.querySelector(selector);
        if (!el) return;
        var pane = el.closest(".show-pane");
        if (!pane) return;
        var top = el.offsetTop - pane.offsetTop;
        if (top < pane.scrollTop || top + el.offsetHeight > pane.scrollTop + pane.clientHeight)
            pane.scrollTo({ top: Math.max(0, top - pane.clientHeight / 3), behavior: "smooth" });
    }
    window.waaShow = { reveal: reveal };
})();
