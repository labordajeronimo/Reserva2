import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { Api, TurnoPorToken } from '../../core/api';

@Component({
  selector: 'app-cancelar-turno',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './cancelar-turno.html',
  styleUrls: ['./cancelar-turno.css']
})
export class CancelarTurno implements OnInit {
  private token = '';

  cargando = signal(true);
  turno = signal<TurnoPorToken | null>(null);
  errorCarga = signal<string | null>(null);

  cancelando = signal(false);
  errorCancelar = signal<string | null>(null);
  canceladoOk = signal(false);

  constructor(private route: ActivatedRoute, private api: Api) {}

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParamMap.get('token') ?? '';
    if (!this.token) {
      this.cargando.set(false);
      this.errorCarga.set('El link no es válido.');
      return;
    }

    this.api.getTurnoPorToken(this.token).subscribe({
      next: t => {
        this.turno.set(t);
        this.cargando.set(false);
      },
      error: () => {
        this.cargando.set(false);
        this.errorCarga.set('No encontramos ese turno. El link puede estar vencido o ser incorrecto.');
      }
    });
  }

  confirmarCancelacion(): void {
    this.cancelando.set(true);
    this.errorCancelar.set(null);
    this.api.cancelarTurnoPorToken(this.token).subscribe({
      next: () => {
        this.cancelando.set(false);
        this.canceladoOk.set(true);
      },
      error: err => {
        this.cancelando.set(false);
        this.errorCancelar.set(err.error?.mensaje ?? 'No pudimos cancelar el turno.');
      }
    });
  }
}
