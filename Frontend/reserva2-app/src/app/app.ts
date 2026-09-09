import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet></router-outlet>' // Acá Angular va a inyectar las distintas páginas
})
export class App {
  // Ya no necesitamos lógica acá, todo lo manejan las rutas
}