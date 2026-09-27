// Desplaza una fila horizontal (p. ej. "Productos en tendencia") una "página" hacia la izquierda o derecha.
window.cgScroller = {
    page: function (element, direction) {
        if (!element) return;
        element.scrollBy({ left: direction * element.clientWidth * 0.9, behavior: "smooth" });
    }
};
