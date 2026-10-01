import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-plan-selector',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './plan-selector.html',
  styleUrls: ['./plan-selector.css']
})
export class PlanSelector {
  @Input() plan = 'Gratuito';
  @Output() planChange = new EventEmitter<string>();

  // Solo el panel los usa: muestra un radio en cada tarjeta y marca cuál es el plan vigente.
  // En el registro quedan apagados y el selector se ve como siempre.
  @Input() conRadio = false;
  @Input() planActual: string | null = null;

  @Input() ciclo = 'Mensual';
  @Output() cicloChange = new EventEmitter<string>();

  elegirPlan(p: string): void {
    this.plan = p;
    this.planChange.emit(p);
  }

  elegirCiclo(c: string): void {
    this.ciclo = c;
    this.cicloChange.emit(c);
  }
}
