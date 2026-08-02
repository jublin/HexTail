window.hexTailFilePicker = (() => {
    const watchers = new Map();

    async function poll(watcher, dotnet) {
        // ponytail: full-file reads keep the browser implementation small; use File.slice with byte offsets if large logs make this measurable.
        const file = await watcher.handle.getFile();
        const text = await file.text();
        if (text.length < watcher.offset) {
            watcher.offset = 0;
            watcher.remainder = "";
            await dotnet.invokeMethodAsync("OnBrowserFileTruncated", watcher.id);
        }

        const chunk = watcher.remainder + text.slice(watcher.offset);
        watcher.offset = text.length;
        const parts = chunk.split("\n");
        if (chunk.endsWith("\n")) {
            parts.pop();
            watcher.remainder = "";
        } else {
            watcher.remainder = parts.pop() ?? "";
        }

        const lines = parts.map(line => line.endsWith("\r") ? line.slice(0, -1) : line);
        if (lines.length > 0)
            await dotnet.invokeMethodAsync("OnBrowserFileLines", watcher.id, watcher.name, lines);
    }

    return {
        async pick(dotnet) {
            if (!window.showOpenFilePicker)
                throw new Error("Active file tailing requires Chrome or Edge over HTTPS/localhost.");

            const handles = await window.showOpenFilePicker({ multiple: true });
            for (const handle of handles) {
                const id = crypto.randomUUID();
                const watcher = { id, name: handle.name, handle, offset: 0, remainder: "" };
                watchers.set(id, watcher);
                await poll(watcher, dotnet);
                watcher.timer = setInterval(() => poll(watcher, dotnet).catch(console.error), 250);
            }
        },

        stop(id) {
            const watcher = watchers.get(id);
            if (!watcher)
                return;
            clearInterval(watcher.timer);
            watchers.delete(id);
        },

        stopAll() {
            for (const watcher of watchers.values())
                clearInterval(watcher.timer);
            watchers.clear();
        }
    };
})();
