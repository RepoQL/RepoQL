// Keyboard shortcut handler for RepoQL Web UI
// Manages Alt+Arrow for back navigation and Ctrl+Enter for execution

export function initKeyboardShortcuts(dotNetRef) {
    document.addEventListener('keydown', (e) => {
        // Alt+Left Arrow - Go back
        if (e.altKey && e.key === 'ArrowLeft') {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OnGoBack');
            return;
        }

        // Ctrl+Enter - Execute (Query, Search, Read)
        if (e.ctrlKey && e.key === 'Enter') {
            // Don't prevent default - let components handle it
            dotNetRef.invokeMethodAsync('OnExecute');
            return;
        }
    });
}

export function dispose() {
    // Cleanup if needed
}
