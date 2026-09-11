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
