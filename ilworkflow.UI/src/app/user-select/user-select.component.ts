import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ElsaUser } from '../elsa.service';

@Component({
  selector: 'app-user-select',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './user-select.component.html',
  styleUrl: './user-select.component.scss',
})
export class UserSelectComponent {
  users = input.required<ElsaUser[]>();
  selectedId = input('');
  userChange = output<string>();
}
