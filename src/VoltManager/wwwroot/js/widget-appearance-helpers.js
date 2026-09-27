(function () {
    function choicePayload(choice, desiredAppearance) {
        return choice === 'global' ? null : Object.assign({}, desiredAppearance || {}, { material: choice });
    }

    function rovingIndex(currentIndex, key, length) {
        if (!length) return -1;
        if (key === 'Home') return 0;
        if (key === 'End') return length - 1;
        if (key === 'ArrowLeft' || key === 'ArrowUp') return (currentIndex - 1 + length) % length;
        if (key === 'ArrowRight' || key === 'ArrowDown') return (currentIndex + 1) % length;
        return currentIndex;
    }

    function counts(items) {
        var list = Array.isArray(items) ? items : [];
        var overridden = list.filter(function (item) { return !!(item && item.appearance); }).length;
        return { globalCount: list.length - overridden, overrideCount: overridden };
    }

    function statusText(items, translate, format) {
        var value = counts(items);
        var appliedKey = value.globalCount === 1 ? 'widget_appearance_applied_one' : 'widget_appearance_applied_many';
        var appliedFallback = value.globalCount === 1 ? 'Applicato a {count} widget.' : 'Applicato a {count} widget.';
        var text = format(translate(appliedKey, appliedFallback), { count: value.globalCount });
        if (!value.overrideCount) return text;
        var overrideKey = value.overrideCount === 1 ? 'widget_appearance_custom_one' : 'widget_appearance_custom_many';
        var overrideFallback = value.overrideCount === 1
            ? '{count} usa un materiale personalizzato.'
            : '{count} usano un materiale personalizzato.';
        return text + ' ' + format(translate(overrideKey, overrideFallback), { count: value.overrideCount });
    }

    async function resetAll(items, setOverride, refetch) {
        var overridden = (Array.isArray(items) ? items : []).filter(function (item) {
            return !!(item && item.type && item.appearance);
        });
        var finalState = null;
        try {
            for (var i = 0; i < overridden.length; i++) {
                finalState = await setOverride(overridden[i].type);
            }
            return finalState;
        } catch {
            return await refetch();
        }
    }

    window.VoltWidgetAppearanceHelpers = {
        choicePayload: choicePayload,
        rovingIndex: rovingIndex,
        counts: counts,
        statusText: statusText,
        resetAll: resetAll,
    };
})();
