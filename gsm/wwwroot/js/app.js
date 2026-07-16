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
