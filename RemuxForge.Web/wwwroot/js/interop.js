// Keyboard capture - filtra tasti e inoltra a .NET
export function captureKeyboard(dotNetRef) {
    // Rimuovi handler precedente se presente
    if (window._rfKeyHandler) {
        document.removeEventListener('keydown', window._rfKeyHandler);
        document.removeEventListener('keydown', window._rfKeyHandler, true);
    }
    if (window._rfPseudoControlObserver) {
        window._rfPseudoControlObserver.disconnect();
        window._rfPseudoControlObserver = null;
    }
    if (window._rfSelectionGuard) {
        document.removeEventListener('selectstart', window._rfSelectionGuard, true);
        window._rfSelectionGuard = null;
    }
    if (window._rfSelectionChangeGuard) {
        document.removeEventListener('selectionchange', window._rfSelectionChangeGuard, true);
        window._rfSelectionChangeGuard = null;
    }

    setupPseudoControls();
    setupSelectionGuard();
    window._rfPseudoControlObserver = new MutationObserver(function () {
        setupPseudoControls();
    });
    window._rfPseudoControlObserver.observe(document.body, { childList: true, subtree: true });

    window._rfKeyHandler = function (e) {
        var key = getNormalizedKey(e);
        var ctrl = e.ctrlKey;
        var shift = e.shiftKey;
        var alt = e.altKey;
        var tagName = document.activeElement ? document.activeElement.tagName : '';
        var activeElement = document.activeElement;
        var blockingDialogOpen = hasBlockingDialogOpen();

        if (activeElement && activeElement.classList && isPseudoControl(activeElement) && (key === 'Enter' || key === ' ')) {
            e.preventDefault();
            activeElement.click();
            return;
        }

        if (blockingDialogOpen) {
            if (key === 'Escape') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
            }
            return;
        }

        // Se un campo editabile ha focus, gestisci solo Tab e Escape
        if (isEditableElement(activeElement, tagName)) {
            if (key === 'Tab' && document.activeElement.classList.contains('path-bar-input')) {
                e.preventDefault();
            }
            if (key === 'Escape') {
                e.preventDefault();
                dotNetRef.invokeMethodAsync('OnKeyDown', key, ctrl, shift, alt);
            }
            return;
        }

        // Filtra: invia solo tasti usati dalla UI per evitare scroll browser durante navigazione tabella/menu
        var isFKey = key.startsWith('F') && key.length <= 3 && !isNaN(key.substring(1));
        var isNavigation = key === 'ArrowUp' || key === 'ArrowDown' || key === 'ArrowLeft' || key === 'ArrowRight'
            || key === 'Home' || key === 'End' || key === 'PageUp' || key === 'PageDown';
        var isSpecial = key === 'Escape' || key === 'Enter' || key === 'Delete' || key === ' ' || key === 'Alt';
        var isCtrlShortcut = ctrl && key.toLowerCase() === 'a';
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
        if (isNavigation) {
            clearTextSelection();
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

// Rimuovi handler tastiera
export function releaseKeyboard() {
    if (window._rfKeyHandler) {
        document.removeEventListener('keydown', window._rfKeyHandler);
        document.removeEventListener('keydown', window._rfKeyHandler, true);
        window._rfKeyHandler = null;
    }
    if (window._rfPseudoControlObserver) {
        window._rfPseudoControlObserver.disconnect();
        window._rfPseudoControlObserver = null;
    }
    if (window._rfSelectionGuard) {
        document.removeEventListener('selectstart', window._rfSelectionGuard, true);
        window._rfSelectionGuard = null;
    }
    if (window._rfSelectionChangeGuard) {
        document.removeEventListener('selectionchange', window._rfSelectionChangeGuard, true);
        window._rfSelectionChangeGuard = null;
    }
}

// Rende focusabili i controlli custom basati su span/div, mantenendo l'ordine DOM per Tab
function setupPseudoControls() {
    var controls = document.querySelectorAll('.ui-toggle, .btn-browse, .cmd-key');
    for (var i = 0; i < controls.length; i++) {
        if (!controls[i].hasAttribute('tabindex')) {
            controls[i].setAttribute('tabindex', '0');
        }
        if (!controls[i].hasAttribute('role')) {
            controls[i].setAttribute('role', 'button');
        }
    }
}

function isPseudoControl(element) {
    return element.classList.contains('ui-toggle')
        || element.classList.contains('btn-browse')
        || element.classList.contains('cmd-key');
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

function hasBlockingDialogOpen() {
    return document.querySelector('.dialog-overlay.visible') !== null;
}

function getNormalizedKey(e) {
    var key = e.key;
    if (e.altKey && e.code && e.code.indexOf('Key') === 0 && e.code.length === 4) {
        key = e.code.substring(3);
    }

    return key;
}

function setupSelectionGuard() {
    window._rfSelectionGuard = function (e) {
        if (!isTextSelectionAllowed(e.target)) {
            e.preventDefault();
            clearTextSelection();
        }
    };
    window._rfSelectionChangeGuard = function () {
        var activeElement = document.activeElement;
        var tagName = activeElement ? activeElement.tagName : '';
        if (isEditableElement(activeElement, tagName)) {
            return;
        }

        var selection = window.getSelection ? window.getSelection() : null;
        if (selection && selection.rangeCount > 0 && !isSelectionAllowed(selection)) {
            selection.removeAllRanges();
        }
    };
    document.addEventListener('selectstart', window._rfSelectionGuard, true);
    document.addEventListener('selectionchange', window._rfSelectionChangeGuard, true);
}

function isTextSelectionAllowed(target) {
    var element = normalizeSelectionNode(target);
    if (!element || !element.closest) {
        return false;
    }

    return element.closest('.log-panel') !== null
        || element.closest('.detail-content') !== null
        || element.closest('input, textarea, select, [contenteditable="true"]') !== null;
}

function isSelectionAllowed(selection) {
    var anchor = normalizeSelectionNode(selection.anchorNode);
    var focus = normalizeSelectionNode(selection.focusNode);
    if (!anchor && !focus) {
        return true;
    }

    return isTextSelectionAllowed(anchor) || isTextSelectionAllowed(focus);
}

function normalizeSelectionNode(node) {
    if (!node) {
        return null;
    }
    if (node.nodeType === Node.ELEMENT_NODE) {
        return node;
    }

    return node.parentElement;
}

function clearTextSelection() {
    var selection = window.getSelection ? window.getSelection() : null;
    if (selection && selection.removeAllRanges) {
        selection.removeAllRanges();
    }
}

// Tema
export function setTheme(themeName) {
    document.documentElement.setAttribute('data-webtui-theme', themeName);
    localStorage.setItem('rf-theme', themeName);
}

export function loadSavedTheme() {
    var saved = localStorage.getItem('rf-theme');
    if (saved) {
        document.documentElement.setAttribute('data-webtui-theme', saved);
    }
    return saved || 'nord';
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

// Scroll log alla fine
export function scrollLogToBottom() {
    var el = document.querySelector('.log-panel .rf-panel-body');
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
}
