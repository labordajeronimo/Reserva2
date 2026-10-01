import { Component, EventEmitter, HostListener, Input, OnChanges, Output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';

import { Api, ComercioDetalle } from '../../../core/api';
import { linkWhatsApp } from '../../../core/whatsapp';

// Panel lateral con el detalle de un comercio (Etapa C del Super Admin). Se abre desde la
// tabla ("Ver" o el nombre) y desde las alertas. La ficha completa en
// /super-admin/comercios/:id sigue existiendo y se puede abrir desde acá.
@Component({
  selector: 'app-comercio-drawer',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './comercio-drawer.html',
  styleUrls: ['./comercio-drawer.css']
})
export class ComercioDrawer implements OnChanges {
  @Input({ required: true }) comercioId!: number;
  // La salud se calcula en la tabla (necesita las estadísticas de todos); se pasa ya resuelta.
  @Input() salud: string | null = null;
  @Input() claseSalud = 'sin';

  @Output() cerrar = new EventEmitter<void>();
  @Output() eliminado = new EventEmitter<number>();

  // Mismo umbral que la alerta "cerca del límite gratis" de la tabla.
  readonly avisoTurnosGratuito = 45;

  detalle = signal<ComercioDetalle | null>(null);
  cargando = signal(false);
  error = signal<string | null>(null);
  eliminando = signal(false);
  errorEliminar = signal<string | null>(null);

  constructor(private api: Api) {}

  ngOnChanges(): void {
    this.cargar();
  }

  @HostListener('document:keydown.escape')
  alApretarEscape(): void {
    this.cerrar.emit();
  }

  private cargar(): void {
    this.detalle.set(null);
    this.error.set(null);
    this.errorEliminar.set(null);
    this.cargando.set(true);
    this.api.getComercioDetalle(this.comercioId).subscribe({
      next: d => {
        this.detalle.set(d);
        this.cargando.set(false);
      },
      error: () => {
        this.cargando.set(false);
        this.error.set('No pudimos cargar este comercio.');
      }
    });
  }

  iniciales(nombre: string): string {
    const palabras = nombre.trim().split(/\s+/).filter(Boolean);
    return palabras.slice(0, 2).map(p => p[0].toUpperCase()).join('') || '?';
  }

  estado(d: ComercioDetalle): 'Activo' | 'Pausado' | 'Pendiente' {
    if (d.activo) return 'Activo';
    return d.fechaActivacion ? 'Pausado' : 'Pendiente';
  }

  nombrePlan(plan: string): string {
    return plan === 'Basico' ? 'Básico' : plan;
  }

  diasParaVencimiento(d: ComercioDetalle): number | null {
    if (!d.fechaProximoPago) return null;
    const hoy = new Date();
    hoy.setHours(0, 0, 0, 0);
    const vencimiento = new Date(d.fechaProximoPago);
    vencimiento.setHours(0, 0, 0, 0);
    return Math.round((vencimiento.getTime() - hoy.getTime()) / (1000 * 60 * 60 * 24));
  }

  textoVencimiento(d: ComercioDetalle): string {
    if (d.planActual === 'Gratuito') return '—';
    const dias = this.diasParaVencimiento(d);
    if (dias === null) return 'Sin definir';
    if (dias > 0) return `En ${dias} día${dias === 1 ? '' : 's'}`;
    if (dias === 0) return 'Hoy';
    return `Venció hace ${-dias} día${dias === -1 ? '' : 's'}`;
  }

  textoUltimoAcceso(d: ComercioDetalle): string {
    if (!d.ultimoAcceso) return 'Sin datos todavía';
    const iso = /Z$|[+-]\d{2}:\d{2}$/.test(d.ultimoAcceso) ? d.ultimoAcceso : `${d.ultimoAcceso}Z`;
    const dias = Math.floor((Date.now() - new Date(iso).getTime()) / (1000 * 60 * 60 * 24));
    return dias === 0 ? 'Hoy' : `Hace ${dias} día${dias === 1 ? '' : 's'}`;
  }

  alturaBarra(cantidad: number, d: ComercioDetalle): number {
    const max = Math.max(1, ...d.turnosPorDia.map(x => x.cantidad));
    return cantidad === 0 ? 2 : Math.max(4, Math.round((cantidad / max) * 100));
  }

  etiquetaFechaCorta(fecha: string): string {
    const [, m, dia] = fecha.split('-');
    return `${dia}/${m}`;
  }

  usoGratis(d: ComercioDetalle): number {
    return Math.min(100, Math.round((d.turnosDelMes / Math.max(1, d.topeTurnosGratuito)) * 100));
  }

  linkWhatsAppComercio(d: ComercioDetalle): string | null {
    if (!d.whatsAppNumero) return null;
    return linkWhatsApp(`Hola! Te escribo de Reserva2 por ${d.nombre}.`, d.whatsAppNumero);
  }

  // Doble confirmación (aviso + escribir el nombre), igual que en la ficha completa. Solo
  // se puede con el comercio pausado.
  eliminar(): void {
    const d = this.detalle();
    if (!d || d.activo) return;
    this.errorEliminar.set(null);

    if (!confirm(`¿Eliminar definitivamente "${d.nombre}"? Se borran también sus turnos, servicios, horarios, profesionales y sucursales. Esto no se puede deshacer.`)) return;
    const escrito = prompt(`Para confirmar, escribí el nombre del comercio: ${d.nombre}`);
    if (escrito === null) return;
    if (escrito.trim() !== d.nombre.trim()) {
      this.errorEliminar.set('El nombre no coincide. No se eliminó nada.');
      return;
    }

    this.eliminando.set(true);
    this.api.eliminarComercio(d.id).subscribe({
      next: () => {
        this.eliminando.set(false);
        this.eliminado.emit(d.id);
      },
      error: err => {
        this.eliminando.set(false);
        this.errorEliminar.set(err.error?.mensaje ?? 'No pudimos eliminar el comercio.');
      }
    });
  }
}
