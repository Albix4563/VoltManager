/**
 * Setup overlay: shown at boot when default power plans are missing.
 */
(function () {
    if (!Host.available) return;

    const overlay = document.getElementById('setup-overlay');
    const modal = document.getElementById('setup-modal');
    const message = document.getElementById('setup-message');
    const btnInstall = document.getElementById('btn-setup-install');
    const btnExit = document.getElementById('btn-setup-exit');

    // Subtle parallax tilt from the design prototype.
    overlay.addEventListener('mousemove', (e) => {
        const x = e.clientX / window.innerWidth;
        const y = e.clientY / window.innerHeight;
        modal.style.transform =
            'perspective(1000px) rotateX(' + ((y - 0.5) * -5) + 'deg) rotateY(' + ((x - 0.5) * 5) + 'deg)';
    });

    function show() { overlay.classList.remove('hidden'); }
    function hide() { overlay.classList.add('hidden'); }

    Host.call('checkDefaultPlans').then(res => {
        if (!res.allPresent) show();
    }).catch(() => {});

    btnExit.addEventListener('click', () => {
        Host.call('exitApp').catch(() => {});
    });

    btnInstall.addEventListener('click', async () => {
        btnInstall.disabled = true;
        message.textContent = 'Installazione dei piani energetici in corso…';
        try {
            const res = await Host.call('restoreDefaultPlans');
            if (res.success) {
                message.textContent = 'Piani installati correttamente.';
                setTimeout(hide, 600);
            } else {
                message.textContent = 'Installazione parziale: alcuni piani non sono stati creati. Riprova.';
                btnInstall.disabled = false;
            }
        } catch (err) {
            message.textContent = 'Errore: ' + err.message;
            btnInstall.disabled = false;
        }
    });
})();
