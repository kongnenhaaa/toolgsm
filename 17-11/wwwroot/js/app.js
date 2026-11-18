window.clipboardCopy = {
    copyText: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            // Fallback cũ
            const ta = document.createElement("textarea");
            ta.value = text;
            document.body.appendChild(ta);
            ta.select();
            document.execCommand("copy");
            document.body.removeChild(ta);
            return true;
        }
    }
};

window.appUi = {
    initNav: function () {
        const nav = document.querySelector(".app-nav");
        if (!nav || nav.dataset.scrollReady) return;
        nav.dataset.scrollReady = "true";

        const leftButton = nav.parentElement.querySelector(".app-nav-arrow-left");
        const rightButton = nav.parentElement.querySelector(".app-nav-arrow-right");
        const updateButtons = () => {
            const maxScroll = Math.max(0, nav.scrollWidth - nav.clientWidth);
            leftButton.disabled = nav.scrollLeft <= 1;
            rightButton.disabled = nav.scrollLeft >= maxScroll - 1;
        };

        nav.addEventListener("scroll", updateButtons, { passive: true });
        new ResizeObserver(updateButtons).observe(nav);
        requestAnimationFrame(updateButtons);

        nav.addEventListener("wheel", (event) => {
            const delta = event.deltaX || event.deltaY;
            const maxScroll = nav.scrollWidth - nav.clientWidth;
            const next = Math.max(0, Math.min(maxScroll, nav.scrollLeft + delta));
            if (Math.abs(next - nav.scrollLeft) > 0.5) {
                event.preventDefault();
                nav.scrollLeft = next;
            }
        }, { passive: false });

        let pointerId = null;
        let startX = 0;
        let startScroll = 0;
        let dragged = false;
        nav.addEventListener("pointerdown", (event) => {
            if (event.pointerType !== "mouse" || event.button !== 0) return;
            pointerId = event.pointerId;
            startX = event.clientX;
            startScroll = nav.scrollLeft;
        });
        nav.addEventListener("pointermove", (event) => {
            if (event.pointerId !== pointerId) return;
            const distance = event.clientX - startX;
            if (!dragged && Math.abs(distance) < 5) return;
            if (!dragged) {
                dragged = true;
                nav.classList.add("is-dragging");
                nav.setPointerCapture(pointerId);
            }
            nav.scrollLeft = startScroll - distance;
        });
        const endDrag = (event) => {
            if (event.pointerId !== pointerId) return;
            if (nav.hasPointerCapture(pointerId)) nav.releasePointerCapture(pointerId);
            nav.classList.remove("is-dragging");
            pointerId = null;
            if (dragged) setTimeout(() => { dragged = false; }, 0);
        };
        nav.addEventListener("pointerup", endDrag);
        nav.addEventListener("pointercancel", endDrag);
        nav.addEventListener("click", (event) => {
            if (!dragged) return;
            event.preventDefault();
            event.stopPropagation();
        }, true);
    },
    scrollNav: function (direction) {
        const nav = document.querySelector(".app-nav");
        if (!nav) return;
        nav.scrollBy({ left: direction * Math.max(180, nav.clientWidth * 0.65), behavior: "smooth" });
    },
    setDarkMode: function (enabled) {
        const isDark = enabled === true;
        document.documentElement.classList.toggle("theme-dark", isDark);
        document.body.classList.toggle("theme-dark", isDark);
        document.documentElement.style.colorScheme = isDark ? "dark" : "light";
    },
    scrollToTop: function () {
        window.scrollTo({ top: 0, left: 0, behavior: "auto" });
        document.documentElement.scrollTop = 0;
        document.body.scrollTop = 0;
        const main = document.querySelector(".app-main-content");
        if (main) main.scrollTop = 0;
    }
};
