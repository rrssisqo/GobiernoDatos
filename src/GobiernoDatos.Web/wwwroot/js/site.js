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

    // <input data-buscar-usuario="url">: busca usuarios en Entra ID mientras se escribe (certificadores, gerentes);
    // al elegir, el campo queda con DOMINIO\usuario y el nombre se muestra en el .seleccionado vecino.
    (() => {
        const campos = document.querySelectorAll('input[data-buscar-usuario]');
        if (!campos.length) return;
        const lista = document.createElement('ul');
        Object.assign(lista, { id: 'sugerencias-usuario', className: 'sugerencias-usuario oculto' });
        lista.setAttribute('role', 'listbox');
        lista.setAttribute('aria-label', 'Usuarios encontrados');
        document.body.append(lista);
        let input = null, resultados = [], activo = -1, temporizador = 0, consulta = 0;
        const cerrar = () => { lista.classList.add('oculto'); input?.setAttribute('aria-expanded', 'false'); activo = -1; };
        const ubicar = () => {
            if (!input) return;
            const r = input.getBoundingClientRect();
            Object.assign(lista.style, { left: r.left + 'px', top: (r.bottom + 4) + 'px', minWidth: Math.max(r.width, 360) + 'px' });
        };
        const texto = (etiqueta, valor) => { const e = document.createElement(etiqueta); e.textContent = valor; return e; };
        const pintar = mensaje => {
            lista.replaceChildren();
            if (mensaje) { const li = texto('li', mensaje); li.className = 'mensaje'; lista.append(li); }
            resultados.forEach((u, i) => {
                const li = document.createElement('li');
                li.setAttribute('role', 'option'); li.id = 'sug-' + i;
                li.setAttribute('aria-selected', String(i === activo));
                li.append(texto('strong', u.nombre), texto('span', [u.usuarioRed, u.correo].filter(Boolean).join(' · ')),
                          texto('span', [u.puesto, u.area].filter(Boolean).join(' · ')));
                li.addEventListener('mousedown', ev => { ev.preventDefault(); elegir(i); });
                lista.append(li);
            });
            ubicar();
            lista.classList.remove('oculto');
            input.setAttribute('aria-expanded', 'true');
            if (activo >= 0) input.setAttribute('aria-activedescendant', 'sug-' + activo); else input.removeAttribute('aria-activedescendant');
        };
        const elegir = i => {
            const u = resultados[i];
            input.value = u.usuarioRed;
            const sel = input.parentElement.querySelector('.seleccionado'); if (sel) sel.textContent = u.nombre;
            cerrar();
        };
        const buscar = async () => {
            const q = input.value.trim(), n = ++consulta;
            if (q.length < 2) { cerrar(); return; }
            resultados = []; activo = -1; pintar('Buscando…');
            try {
                const r = await fetch(input.dataset.buscarUsuario + '&q=' + encodeURIComponent(q), { headers: { Accept: 'application/json' } });
                const datos = await r.json();
                if (n !== consulta) return;
                resultados = datos.usuarios || []; activo = resultados.length ? 0 : -1;
                pintar(datos.error || (resultados.length ? '' : 'Sin coincidencias con usuario de red.'));
            } catch {
                if (n === consulta) { resultados = []; pintar('No se pudo buscar; escribe el usuario de red (MABENET\\usuario).'); }
            }
        };
        campos.forEach(el => {
            el.addEventListener('input', () => {
                input = el; const sel = el.parentElement.querySelector('.seleccionado'); if (sel) sel.textContent = '';
                clearTimeout(temporizador); temporizador = setTimeout(buscar, 300);
            });
            el.addEventListener('keydown', ev => {
                if (lista.classList.contains('oculto') || input !== el || !resultados.length) return;
                if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
                    ev.preventDefault();
                    activo = (activo + (ev.key === 'ArrowDown' ? 1 : resultados.length - 1)) % resultados.length;
                    pintar('');
                } else if (ev.key === 'Enter' && activo >= 0) { ev.preventDefault(); elegir(activo); }
                else if (ev.key === 'Escape') cerrar();
            });
            el.addEventListener('blur', () => setTimeout(cerrar, 150));
        });
        window.addEventListener('scroll', ubicar, true);
        window.addEventListener('resize', ubicar);
    })();
})();
