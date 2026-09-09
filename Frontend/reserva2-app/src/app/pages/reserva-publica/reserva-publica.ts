import { Component, OnInit, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { Api, ComercioPublico, Servicio, SlotDisponibilidad } from '../../core/api';

interface DiaGrilla {
  fecha: string; // yyyy-MM-dd, lo que le mandamos a la API
  etiquetaDia: string; // "mar", "mié"...
  numeroDia: number;
  etiquetaCompleta: string; // "Miércoles 10"
}

const DIAS_CORTOS = ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'];
const DIAS_LARGOS = ['Domingo', 'Lunes', 'Martes', 'Miércoles', 'Jueves', 'Viernes', 'Sábado'];

@Component({
  selector: 'app-reserva-publica',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './reserva-publica.html',
  styleUrls: ['./reserva-publica.css']
})
export class ReservaPublica implements OnInit {
  comercio = signal<ComercioPublico | null>(null);
  cargandoComercio = signal(true);

  servicios = signal<Servicio[]>([]);
  servicioSeleccionado = signal<Servicio | null>(null);

  diasDisponibles: DiaGrilla[] = this.generarProximosDias(6);
  diaSeleccionado = signal<DiaGrilla | null>(null);

  slots = signal<SlotDisponibilidad[]>([]);
  slotSeleccionado = signal<SlotDisponibilidad | null>(null);
  cargandoSlots = signal(false);

  clienteNombre = '';
  clienteWhatsApp = '';
  reservando = signal(false);
  errorReserva = signal<string | null>(null);
  reservaConfirmada = signal(false);
  linkCopiado = signal(false);

  hostActual = typeof window !== 'undefined' ? window.location.host : 'reserva2.app';

  iniciales = computed(() => {
    const nombre = this.comercio()?.nombre ?? '';
    return nombre
      .split(' ')
      .filter(p => p.length > 0)
      .slice(0, 2)
      .map(p => p[0]!.toUpperCase())
      .join('');
  });

  puedeReservar = computed(() =>
    !!this.servicioSeleccionado() &&
    !!this.slotSeleccionado() &&
    this.slotSeleccionado()!.disponible &&
    this.clienteNombre.trim().length > 1 &&
    this.clienteWhatsApp.trim().length > 5
  );

  constructor(private route: ActivatedRoute, private api: Api) {}

  ngOnInit(): void {
    const alias = this.route.snapshot.paramMap.get('alias');
    if (!alias) {
      this.cargandoComercio.set(false);
      return;
    }

    this.api.getComercioPorAlias(alias).subscribe({
      next: comercio => {
        this.comercio.set(comercio);
        this.cargandoComercio.set(false);
        this.cargarServicios(comercio.id);
        this.elegirDia(this.diasDisponibles[0]);
      },
      error: () => this.cargandoComercio.set(false)
    });
  }

  private cargarServicios(comercioId: number): void {
    this.api.getServiciosPorComercio(comercioId).subscribe(servicios => {
      this.servicios.set(servicios);
      if (servicios.length > 0) this.elegirServicio(servicios[0]);
    });
  }

  elegirServicio(servicio: Servicio): void {
    this.servicioSeleccionado.set(servicio);
    this.slotSeleccionado.set(null);
    this.buscarDisponibilidad();
  }

  elegirDia(dia: DiaGrilla): void {
    this.diaSeleccionado.set(dia);
    this.slotSeleccionado.set(null);
    this.buscarDisponibilidad();
  }

  elegirSlot(slot: SlotDisponibilidad): void {
    if (!slot.disponible) return;
    this.slotSeleccionado.set(slot);
    this.errorReserva.set(null);
  }

  private buscarDisponibilidad(): void {
    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    const dia = this.diaSeleccionado();
    if (!comercio || !servicio || !dia) return;

    this.cargandoSlots.set(true);
    this.api.getDisponibilidad(comercio.id, servicio.id, dia.fecha).subscribe({
      next: slots => {
        this.slots.set(slots);
        this.cargandoSlots.set(false);
      },
      error: () => {
        this.slots.set([]);
        this.cargandoSlots.set(false);
      }
    });
  }

  formatearHora(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleTimeString('es-AR', { hour: '2-digit', minute: '2-digit', hour12: false });
  }

  copiarLink(): void {
    const url = `${this.hostActual}/${this.comercio()?.aliasUrl ?? ''}`;
    navigator.clipboard?.writeText(url).then(() => {
      this.linkCopiado.set(true);
      setTimeout(() => this.linkCopiado.set(false), 2000);
    });
  }

  confirmarReserva(): void {
    const comercio = this.comercio();
    const servicio = this.servicioSeleccionado();
    const slot = this.slotSeleccionado();
    if (!comercio || !servicio || !slot || !this.puedeReservar()) return;

    this.reservando.set(true);
    this.errorReserva.set(null);

    this.api.crearTurno({
      comercioId: comercio.id,
      servicioId: servicio.id,
      fechaHoraInicio: slot.inicio,
      clienteNombre: this.clienteNombre.trim(),
      clienteWhatsApp: this.clienteWhatsApp.trim()
    }).subscribe({
      next: () => {
        this.reservando.set(false);
        this.reservaConfirmada.set(true);
      },
      error: err => {
        this.reservando.set(false);
        if (err.status === 409) {
          this.errorReserva.set('Ese horario ya no está disponible. Elegí otro.');
          this.buscarDisponibilidad();
          this.slotSeleccionado.set(null);
        } else {
          this.errorReserva.set('No pudimos guardar la reserva. Probá de nuevo.');
        }
      }
    });
  }

  reservarOtroTurno(): void {
    this.reservaConfirmada.set(false);
    this.slotSeleccionado.set(null);
    this.clienteNombre = '';
    this.clienteWhatsApp = '';
    this.buscarDisponibilidad();
  }

  private generarProximosDias(cantidad: number): DiaGrilla[] {
    const dias: DiaGrilla[] = [];
    const hoy = new Date();

    for (let i = 0; i < cantidad; i++) {
      const fecha = new Date(hoy);
      fecha.setDate(hoy.getDate() + i);

      const yyyy = fecha.getFullYear();
      const mm = String(fecha.getMonth() + 1).padStart(2, '0');
      const dd = String(fecha.getDate()).padStart(2, '0');
      const diaSemana = fecha.getDay();

      dias.push({
        fecha: `${yyyy}-${mm}-${dd}`,
        etiquetaDia: DIAS_CORTOS[diaSemana],
        numeroDia: fecha.getDate(),
        etiquetaCompleta: `${DIAS_LARGOS[diaSemana]} ${fecha.getDate()}`
      });
    }

    return dias;
  }
}