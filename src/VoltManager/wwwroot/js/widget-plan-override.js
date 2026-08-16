(function (global) {
    'use strict';

    const CANCELLED_MESSAGE = 'Widget plan override cancelled';
    const DURATION_OPTIONS = [
        { hours: 1, key: 'override_1h', fallback: '1 ora' },
        { hours: 10, key: 'override_10h', fallback: '10 ore' },
        { hours: 12, key: 'override_12h', fallback: '12 ore' },
        { forever: true, key: 'override_forever', fallback: 'Sempre' },
    ];

    function shouldPrompt(method, payload, isPlanWidget) {
        return !!isPlanWidget
            && method === 'setManualOverride'
            && !!payload
            && typeof payload.plan === 'string'
            && payload.plan.length > 0
            && !Object.prototype.hasOwnProperty.call(payload, 'hours');
    }

    function buildPayload(plan, choice) {
        if (!choice) return null;
        if (choice.forever === true) return { plan };

        const hours = Number(choice.hours);
        if (!Number.isFinite(hours) || hours <= 0) {
            throw new TypeError('Invalid widget override duration');
        }
        return { plan, hours };
    }

    function createCallInterceptor(originalCall, options) {
        if (typeof originalCall !== 'function') {
            throw new TypeError('originalCall must be a function');
        }

        const opts = options || {};
        return async function interceptedCall(method, payload) {
            const isPlanWidget = typeof opts.isPlanWidget === 'function'
                ? !!opts.isPlanWidget()
                : !!opts.isPlanWidget;

            if (!shouldPrompt(method, payload, isPlanWidget)) {
                return originalCall(method, payload);
            }

            if (typeof opts.chooseDuration !== 'function') {
                throw new Error('Widget override duration chooser is unavailable');
            }

            const choice = await opts.chooseDuration(payload.plan);
            const nextPayload = buildPayload(payload.plan, choice);
            if (!nextPayload) {
                const error = new Error(CANCELLED_MESSAGE);
                error.code = 'WIDGET_OVERRIDE_CANCELLED';
                throw error;
            }

            return originalCall(method, nextPayload);
        };
    }

    function translate(key, fallback) {
        try {
            if (global.I18n && typeof global.I18n.t === 'function') {
                const value = global.I18n.t(key);
                if (value && value !== key) return value;
            }
        } catch (_) { }
        return fallback;
    }

    function planLabel(plan) {
        const normalized = String(plan || '').toLowerCase();
        if (normalized === 'powersaver') {
            return translate('dash_plan_saver', 'Risparmio energia');
        }
        if (normalized === 'balanced') {
            return translate('dash_plan_balanced', 'Bilanciato');
        }
        if (normalized === 'performance') {
            return translate('dash_plan_performance', 'Prestazioni elevate');
        }
        return String(plan || '');
    }

    function chooseDurationDialog(plan) {
        const doc = global.document;
        if (!doc || !doc.body) return Promise.resolve(null);

        return new Promise((resolve) => {
            const previousFocus = doc.activeElement;
            const overlay = doc.createElement('div');
            overlay.className = 'widget-override-overlay';
            overlay.setAttribute('role', 'presentation');

            const dialog = doc.createElement('section');
            dialog.className = 'widget-override-dialog';
            dialog.setAttribute('role', 'dialog');
            dialog.setAttribute('aria-modal', 'true');
            dialog.setAttribute('aria-labelledby', 'widget-override-title');
            dialog.setAttribute('aria-describedby', 'widget-override-plan');
            overlay.appendChild(dialog);

            let settled = false;

            function cleanup(result) {
                if (settled) return;
                settled = true;
                doc.removeEventListener('keydown', onKeyDown, true);
                overlay.remove();
                try {
                    if (previousFocus && typeof previousFocus.focus === 'function') {
                        previousFocus.focus();
                    }
                } catch (_) { }
                resolve(result);
            }

            function onKeyDown(event) {
                if (event.key === 'Escape') {
                    event.preventDefault();
                    event.stopPropagation();
                    cleanup(null);
                }
            }

            function createCloseButton() {
                const button = doc.createElement('button');
                button.type = 'button';
                button.className = 'widget-override-close';
                button.setAttribute('aria-label', translate('override_cancel', 'Annulla'));
                button.textContent = '×';
                button.addEventListener('click', () => cleanup(null));
                return button;
            }

            function renderDurationChoices() {
                dialog.replaceChildren();

                const header = doc.createElement('div');
                header.className = 'widget-override-header';

                const heading = doc.createElement('div');
                heading.className = 'widget-override-heading';

                const title = doc.createElement('strong');
                title.id = 'widget-override-title';
                title.className = 'widget-override-title';
                title.textContent = translate('override_title', 'Per quanto tempo mantenere questo piano?');

                const subtitle = doc.createElement('span');
                subtitle.id = 'widget-override-plan';
                subtitle.className = 'widget-override-plan';
                subtitle.textContent = planLabel(plan);

                heading.append(title, subtitle);
                header.append(heading, createCloseButton());

                const choices = doc.createElement('div');
                choices.className = 'widget-override-options';

                DURATION_OPTIONS.forEach((option) => {
                    const button = doc.createElement('button');
                    button.type = 'button';
                    button.className = 'widget-override-option';
                    button.textContent = translate(option.key, option.fallback);
                    if (option.forever) {
                        button.dataset.forever = 'true';
                        button.addEventListener('click', renderForeverConfirmation);
                    } else {
                        button.dataset.hours = String(option.hours);
                        button.addEventListener('click', () => cleanup({ hours: option.hours }));
                    }
                    choices.appendChild(button);
                });

                dialog.append(header, choices);
                global.requestAnimationFrame?.(() => choices.querySelector('button')?.focus());
            }

            function renderForeverConfirmation() {
                dialog.replaceChildren();

                const header = doc.createElement('div');
                header.className = 'widget-override-header';

                const heading = doc.createElement('div');
                heading.className = 'widget-override-heading';

                const title = doc.createElement('strong');
                title.id = 'widget-override-title';
                title.className = 'widget-override-title';
                title.textContent = translate('override_forever', 'Sempre');

                const warning = doc.createElement('span');
                warning.id = 'widget-override-plan';
                warning.className = 'widget-override-warning';
                warning.textContent = translate(
                    'override_forever_warning',
                    "L'app non cambierà più il piano automaticamente finché non annulli."
                );

                heading.append(title, warning);
                header.append(heading, createCloseButton());

                const actions = doc.createElement('div');
                actions.className = 'widget-override-actions';

                const cancel = doc.createElement('button');
                cancel.type = 'button';
                cancel.className = 'widget-override-option';
                cancel.textContent = translate('override_cancel', 'Annulla');
                cancel.addEventListener('click', renderDurationChoices);

                const confirm = doc.createElement('button');
                confirm.type = 'button';
                confirm.className = 'widget-override-option widget-override-option-primary';
                confirm.textContent = translate('override_confirm', 'Conferma');
                confirm.addEventListener('click', () => cleanup({ forever: true }));

                actions.append(cancel, confirm);
                dialog.append(header, actions);
                global.requestAnimationFrame?.(() => confirm.focus());
            }

            doc.addEventListener('keydown', onKeyDown, true);
            doc.body.appendChild(overlay);
            renderDurationChoices();
        });
    }

    function isPlanWidget() {
        if (!global.location || !global.URLSearchParams) return false;
        try {
            return new global.URLSearchParams(global.location.search).get('w') === 'plans';
        } catch (_) {
            return false;
        }
    }

    function install() {
        if (!global.Host || typeof global.Host.call !== 'function' || !global.document) return false;
        if (!isPlanWidget()) return false;
        if (global.Host.call.__voltWidgetPlanOverride === true) return true;

        const originalCall = global.Host.call.bind(global.Host);
        const intercepted = createCallInterceptor(originalCall, {
            isPlanWidget: true,
            chooseDuration: chooseDurationDialog,
        });
        intercepted.__voltWidgetPlanOverride = true;
        global.Host.call = intercepted;
        return true;
    }

    global.VoltWidgetPlanOverride = {
        shouldPrompt,
        buildPayload,
        createCallInterceptor,
        chooseDurationDialog,
        install,
    };

    install();
})(window);
