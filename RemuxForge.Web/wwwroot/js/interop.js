// Keyboard capture - filtra tasti e inoltra a .NET
export function captureKeyboard(dotNetRef) {
    // Rimuovi handler precedente se presente
    if (window._rfKeyHandler) {
        document.removeEventListener('keydown', window._rfKeyHandler);
        document.removeEventListener('keydown', window._rfKeyHandler, true);
    }
    releaseDialogFocusObserver();
    window._rfDialogFocusStack = [];
    window._rfDialogFocusObserver = new MutationObserver(syncDialogFocus);
    window._rfDialogFocusObserver.observe(document.body, { childList: true, subtree: true });

    window._rfKeyHandler = function (e) {
        var key = getNormalizedKey(e);
        var ctrl = e.ctrlKey;
        var shift = e.shiftKey;
        var alt = e.altKey;
        var tagName = document.activeElement ? document.activeElement.tagName : '';
        var activeElement = document.activeElement;
        var activeDialog = getActiveDialog();

        if (activeDialog) {
            if (key === 'Tab') {
                if (activeElement && activeElement.classList.contains('path-bar-input')) {
                    e.preventDefault();
                }
                else {
                    trapDialogFocus(e, activeDialog);
                }
            }
            if (key === 'Escape') {
                e.preventDefault();
                if (!closeActiveDialog(activeDialog)) {
                    dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
                }
            }
            return;
        }

        var isFKey = key.startsWith('F') && key.length <= 3 && !isNaN(key.substring(1));
        var isMetadataClearShortcut = ctrl && key.toLowerCase() === 'l';

        // Se un campo editabile ha focus, lascia passare solo i comandi UI espliciti
        if (isEditableElement(activeElement, tagName)) {
            if (key === 'Tab' && document.activeElement.classList.contains('path-bar-input')) {
                e.preventDefault();
            }
            if (key === 'Escape' || isFKey || isMetadataClearShortcut) {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
            }
            return;
        }

        // Filtra: invia solo tasti usati dalla UI per evitare scroll browser durante navigazione tabella/menu
        var isNavigation = key === 'ArrowUp' || key === 'ArrowDown' || key === 'ArrowLeft' || key === 'ArrowRight'
            || key === 'Home' || key === 'End' || key === 'PageUp' || key === 'PageDown';
        var isSpecial = key === 'Escape' || key === 'Enter' || key === 'Delete' || key === ' ' || key === 'Alt';
        var isCtrlShortcut = ctrl && (key.toLowerCase() === 'a' || key.toLowerCase() === 'l');
        if (isCtrlShortcut && isTextSelectionAllowed(e.target)) {
            return;
        }
        if (!isFKey && !isNavigation && !isSpecial && !isCtrlShortcut && !alt) {
            return;
        }

        // Previeni default browser per tasti gestiti dalla UI
        if (isFKey || isNavigation || isSpecial || isCtrlShortcut || alt) {
            e.preventDefault();
        }
        // Invia a .NET
        dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
    };

    document.addEventListener('keydown', window._rfKeyHandler, true);
}

// Porta una riga episodio in vista senza delegare lo scroll alle frecce del browser
export function scrollEpisodeRowIntoView(index) {
    var row = document.querySelector('[data-episode-row-index="' + index + '"]');
    if (row) {
        row.focus({ preventScroll: true });
        row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
}

// Porta una riga split in vista senza delegare lo scroll alle frecce del browser
export function scrollSplitRowIntoView(index) {
    var row = document.querySelector('[data-split-row-index="' + index + '"]');
    if (row) {
        row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
}

// Porta una riga metadata in vista senza delegare lo scroll alle frecce del browser
export function scrollMetadataRowIntoView(index) {
    var row = document.querySelector('[data-metadata-row-index="' + index + '"]');
    if (row) {
        row.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
}

// Rimuovi handler tastiera
export function releaseKeyboard() {
    if (window._rfKeyHandler) {
        document.removeEventListener('keydown', window._rfKeyHandler);
        document.removeEventListener('keydown', window._rfKeyHandler, true);
        window._rfKeyHandler = null;
    }
    releaseDialogFocusObserver();
}

function isEditableElement(element, tagName) {
    if (!element) {
        return false;
    }
    if (tagName === 'INPUT' || tagName === 'TEXTAREA' || tagName === 'SELECT') {
        return true;
    }
    return element.isContentEditable === true;
}

function getActiveDialog() {
    var dialogs = document.querySelectorAll('[role="dialog"][aria-modal="true"]');
    return dialogs.length > 0 ? dialogs[dialogs.length - 1] : null;
}

function closeActiveDialog(dialog) {
    var closeButton = dialog.querySelector('[data-dialog-close], .dialog-close-button');
    if (closeButton) {
        closeButton.click();
        return true;
    }

    var overlay = dialog.closest('.dialog-overlay');
    if (overlay) {
        overlay.click();
        return true;
    }

    return false;
}

function trapDialogFocus(event, dialog) {
    var focusable = getFocusableElements(dialog);

    if (focusable.length === 0) {
        event.preventDefault();
        dialog.focus({ preventScroll: true });
        return;
    }

    var first = focusable[0];
    var last = focusable[focusable.length - 1];
    var current = document.activeElement;
    if (!dialog.contains(current) || (!event.shiftKey && current === last)) {
        event.preventDefault();
        first.focus({ preventScroll: true });
    }
    else if (event.shiftKey && current === first) {
        event.preventDefault();
        last.focus({ preventScroll: true });
    }
}

function getFocusableElements(container) {
    return Array.from(container.querySelectorAll(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])'
    )).filter(function (element) {
        return element.getClientRects().length > 0;
    });
}

function syncDialogFocus() {
    var stack = window._rfDialogFocusStack || [];
    var activeDialog = getActiveDialog();
    var activeIndex = stack.findIndex(function (entry) {
        return entry.dialog === activeDialog;
    });

    if (!activeDialog) {
        if (stack.length > 0) {
            var originalOpener = stack[0].opener;
            stack = [];
            if (originalOpener && originalOpener.isConnected) {
                originalOpener.focus({ preventScroll: true });
            }
        }
        window._rfDialogFocusStack = stack;
        return;
    }

    if (activeIndex >= 0) {
        if (activeIndex < stack.length - 1) {
            var closedDialog = stack[stack.length - 1];
            stack = stack.slice(0, activeIndex + 1);
            if (closedDialog.opener && closedDialog.opener.isConnected) {
                closedDialog.opener.focus({ preventScroll: true });
            }
        }
        window._rfDialogFocusStack = stack;
        return;
    }

    var opener = document.activeElement;
    while (stack.length > 0 && !stack[stack.length - 1].dialog.isConnected) {
        var replacedDialog = stack.pop();
        if ((!opener || opener === document.body) && replacedDialog.opener) {
            opener = replacedDialog.opener;
        }
    }
    stack.push({ dialog: activeDialog, opener: opener });
    window._rfDialogFocusStack = stack;

    requestAnimationFrame(function () {
        var focusable = getFocusableElements(activeDialog);
        if (focusable.length > 0) {
            focusable[0].focus({ preventScroll: true });
        }
        else {
            activeDialog.focus({ preventScroll: true });
        }
    });
}

function releaseDialogFocusObserver() {
    if (window._rfDialogFocusObserver) {
        window._rfDialogFocusObserver.disconnect();
        window._rfDialogFocusObserver = null;
    }
    window._rfDialogFocusStack = [];
}

function getNormalizedKey(e) {
    var key = e.key;
    if (e.altKey && e.code && e.code.indexOf('Key') === 0 && e.code.length === 4) {
        key = e.code.substring(3);
    }

    return key;
}

function isTextSelectionAllowed(target) {
    if (!target) {
        return false;
    }

    var element = target.nodeType === Node.ELEMENT_NODE ? target : target.parentElement;
    if (!element || !element.closest) {
        return false;
    }

    return element.closest('.log-panel') !== null
        || element.closest('.detail-content') !== null
        || element.closest('input, textarea, select, [contenteditable="true"]') !== null;
}

// Lingua documento
export function setLanguage(language) {
    document.documentElement.setAttribute('lang', language);
}

// Copia testo nella clipboard
export function copyToClipboard(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text);
    }
}

// Legge valore e selection di un input testuale senza dipendere dal focus corrente
export function getTextInputSelection(elementId) {
    var element = document.getElementById(elementId);
    if (!element) {
        return ['', '0', '0'];
    }

    var value = element.value || '';
    var start = typeof element.selectionStart === 'number' ? element.selectionStart : value.length;
    var end = typeof element.selectionEnd === 'number' ? element.selectionEnd : start;
    return [value, start.toString(), end.toString()];
}

// Aggiorna un input testuale e ripristina focus e selection dopo l'inserimento
export function setTextInputValueAndSelection(elementId, value, selectionStart, selectionEnd) {
    var element = document.getElementById(elementId);
    if (!element) {
        return;
    }

    element.value = value || '';
    element.focus({ preventScroll: true });
    if (typeof element.setSelectionRange === 'function') {
        try {
            element.setSelectionRange(selectionStart, selectionEnd);
        }
        catch {
            // Alcuni input specializzati espongono il metodo ma non supportano selection
        }
    }
}

// Scroll log alla fine
export function scrollLogToBottom() {
    var el = document.querySelector('.log-panel .rf-panel-body');
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
}
