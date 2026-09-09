import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { Api } from '../../core/api';

@Component({
  selector: 'app-panel-olvide-password',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './panel-olvide-password.html',
  styleUrls: ['./panel-olvide-password.css']
})
export class PanelOlvidePassword {
  email = '';
  enviando = signal(false);
  enviado = signal(false);

  constructor(private api: Api) {}

  enviar(): void {
    if (!this.email.trim()) return;

    this.enviando.set(true);
    this.api.olvidoPassword(this.email.trim()).subscribe({
      next: () => {
        this.enviando.set(false);
        this.enviado.set(true);
      },
      error: () => {
        // El backend siempre responde 200 exista o no la cuenta; un error acá es de red/servidor.
        this.enviando.set(false);
        this.enviado.set(true);
      }
    });
  }
}
