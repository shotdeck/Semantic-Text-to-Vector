window.addEventListener('load', () => {
    const injectBrand = () => {
        const wrap = document.querySelector('.swagger-ui .topbar .topbar-wrapper');
        if (!wrap) return;

        // Remove any built-in logo remnants
        wrap.querySelectorAll('.link .logo__img, .link .logo__title, .link svg').forEach(n => n.remove());

        // Insert our logo once
        if (!wrap.querySelector('.sd-brand')) {
            const a = document.createElement('a');
            a.className = 'sd-brand';
            a.href = '#';
            a.innerHTML = `<img class="sd-brand-logo" src="/swagger/shotdeck-logo.png" alt="ShotDeck Logo">`;
            wrap.prepend(a);
        }
    };

    injectBrand();
    setTimeout(injectBrand, 250); // handle late re-render
});
