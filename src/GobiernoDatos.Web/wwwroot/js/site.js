// Comportamientos comunes declarados con atributos data-*.
(() => {
    // Botón del menú: oculta o muestra la barra lateral y recuerda la preferencia en este navegador.
    const toggle = document.getElementById('menu-toggle');
    const raiz = document.documentElement;
    const reflejar = () => {
        const oculto = raiz.classList.contains('menu-oculto');
        const texto = oculto ? 'Mostrar menú' : 'Ocultar menú';
        toggle.setAttribute('aria-expanded', String(!oculto));
        toggle.setAttribute('aria-label', texto);
        toggle.title = texto;
    };
    if (toggle) {
        reflejar();
        toggle.addEventListener('click', () => {
            raiz.classList.toggle('menu-oculto');
            try { localStorage.setItem('gd-menu', raiz.classList.contains('menu-oculto') ? 'oculto' : 'visible'); } catch { }
            reflejar();
        });
    }

    // <select data-autosubmit>: aplica el filtro al cambiar.
    document.addEventListener('change', e => {
        if (e.target.matches('select[data-autosubmit]')) e.target.form.submit();
    });

    // <form data-confirmar="mensaje">: confirmación antes de una acción irreversible.
    document.addEventListener('submit', e => {
        const msg = e.target.dataset.confirmar;
        if (msg && !confirm(msg)) e.preventDefault();
    });

    // <tr class="fila-link" data-href>: toda la fila navega (los controles internos conservan su acción).
    document.addEventListener('click', e => {
        const tr = e.target.closest('tr.fila-link[data-href]');
        if (!tr || e.target.closest('a, button, input, select, textarea, label')) return;
        window.location.href = tr.dataset.href;
    });

    // <input data-filtro-tabla="#id">: filtro de texto sobre data-texto de cada fila.
    document.querySelectorAll('input[data-filtro-tabla]').forEach(input => {
        const tabla = document.querySelector(input.dataset.filtroTabla);
        if (!tabla) return;
        input.addEventListener('input', () => {
            const q = input.value.trim().toLowerCase();
            for (const tr of tabla.tBodies[0].rows)
                tr.classList.toggle('oculto', q !== '' && !(tr.dataset.texto || tr.textContent).toLowerCase().includes(q));
        });
    });
})();
