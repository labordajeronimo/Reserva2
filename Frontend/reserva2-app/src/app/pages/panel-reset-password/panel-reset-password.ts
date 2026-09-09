import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';

import { Api } from '../../core/api';

@Component({
  selector: 'app-panel-reset-password',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './panel-reset-password.html',
  styleUrls: ['./panel-reset-password.css']
})
export class PanelResetPassword {
  token: string | null = null;
  nuevaPassword = '';
  confirmarPassword = '';
  enviando = signal(false);
  completado = signal(false);
  error = signal<string | null>(null);

  constructor(private route: ActivatedRoute, private api: Api, private router: Router) {
    this.token = this.route.snapshot.queryParamMap.get('token');
  }

  guardar(): void {
    this.error.set(null);

    if (!this.token) {
      this.error.set('El link no es válido. Pedí uno nuevo.');
      return;
    }
    if (this.nuevaPassword.length < 6) {
      this.error.set('La contraseña tiene que tener al menos 6 caracteres.');
      return;
    }
    if (this.nuevaPassword !== this.confirmarPassword) {
      this.error.set('Las contraseñas no coinciden.');
      return;
    }

    this.enviando.set(true);
    this.api.restablecerPassword(this.token, this.nuevaPassword).subscribe({
      next: () => {
        this.enviando.set(false);
        this.completado.set(true);
      },
      error: err => {
        this.enviando.set(false);
        this.error.set(err.error?.mensaje ?? 'No pudimos restablecer la contraseña. Probá pedir un nuevo link.');
      }
    });
  }

  irALogin(): void {
    this.router.navigateByUrl('/panel/login');
  }
}
