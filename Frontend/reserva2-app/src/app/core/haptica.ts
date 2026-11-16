// Vibración corta SOLO cuando pasa algo de verdad (un turno reservado, cancelado o
// confirmado; un error al guardar). Nunca en toques comunes, scroll o navegación.
// navigator.vibrate existe en Android/Chrome; en iPhone (Safari) no hay API y no hace nada.
// Si la persona pidió reducir movimiento en su sistema, tampoco vibra.
export type Evento = 'exito' | 'error';

const PATRONES: Record<Evento, number | number[]> = {
  exito: 18,
  error: [30, 60, 30]
};

export function vibrar(evento: Evento): void {
  if (typeof navigator === 'undefined' || typeof navigator.vibrate !== 'function') return;
  if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return;
  try {
    navigator.vibrate(PATRONES[evento]);
  } catch {
    // Algunos navegadores tiran si la página no tuvo interacción todavía: se ignora.
  }
}
