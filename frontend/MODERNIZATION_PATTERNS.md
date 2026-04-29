# Modernización Frontend - Guía de Implementación

Esta guía proporciona patrones reutilizables para modernizar los módulos restantes del proyecto con Angular Material 19.

## Patrones Material Comunes

### 1. Tablas Material (mat-table)

**Componente TypeScript:**

```typescript
import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { MatTableModule, MatTableDataSource } from '@angular/material/table';
import { MatSortModule, Sort } from '@angular/material/sort';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { CrmApiService } from '../../crm-api.service';

interface OpportunityItem {
  id: string;
  title: string;
  status: 'active' | 'pending' | 'closed';
  createdDate: Date;
  responsibleUser: string;
}

@Component({
  selector: 'app-opportunities-table',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatSortModule,
    MatPaginatorModule,
    MatIconModule,
    MatButtonModule,
    MatProgressSpinnerModule,
  ],
  template: `
    <div class="table-container">
      <table mat-table [dataSource]="dataSource" matSort (matSortChange)="onSortChange($event)">
        <!-- ID Column -->
        <ng-container matColumnDef="id">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>ID</th>
          <td mat-cell *matCellDef="let element">{{ element.id }}</td>
        </ng-container>

        <!-- Title Column -->
        <ng-container matColumnDef="title">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Título</th>
          <td mat-cell *matCellDef="let element">{{ element.title }}</td>
        </ng-container>

        <!-- Status Column -->
        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef mat-sort-header>Estado</th>
          <td mat-cell *matCellDef="let element">
            <mat-chip [color]="element.status === 'active' ? 'primary' : 'warn'">
              {{ element.status === 'active' ? 'Activo' : 'Pendiente' }}
            </mat-chip>
          </td>
        </ng-container>

        <!-- Actions Column -->
        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef>Acciones</th>
          <td mat-cell *matCellDef="let element">
            <button mat-icon-button matTooltip="Ver detalles">
              <mat-icon>visibility</mat-icon>
            </button>
            <button mat-icon-button matTooltip="Editar">
              <mat-icon>edit</mat-icon>
            </button>
            <button mat-icon-button matTooltip="Eliminar" color="warn">
              <mat-icon>delete</mat-icon>
            </button>
          </td>
        </ng-container>

        <tr mat-header-row *matHeaderRowDef="displayedColumns; sticky: true"></tr>
        <tr mat-row *matRowDef="let row; columns: displayedColumns"></tr>

        <!-- No Data Row -->
        <tr class="no-data-row" *matNoDataRow>
          <td [attr.colspan]="displayedColumns.length">
            <div class="no-data-container">
              <mat-icon>inbox</mat-icon>
              <p>No hay datos disponibles</p>
            </div>
          </td>
        </tr>
      </table>

      <mat-paginator
        #paginator
        [pageSizeOptions]="[10, 25, 50]"
        [pageSize]="25"
        (page)="onPageChange($event)"
      ></mat-paginator>
    </div>
  `,
  styles: [`
    .table-container {
      width: 100%;
      border-radius: 4px;
      overflow: hidden;
      box-shadow: 0 2px 4px rgba(0,0,0,0.1);
    }

    table {
      width: 100%;
    }

    .no-data-row {
      height: 200px;
    }

    .no-data-container {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      height: 100%;
      color: #999;

      mat-icon {
        font-size: 48px;
        width: 48px;
        height: 48px;
        margin-bottom: 16px;
        opacity: 0.5;
      }
    }

    ::ng-deep {
      .mat-mdc-header-cell {
        background-color: #f5f5f5;
        font-weight: 600;
      }

      .mat-mdc-row:hover {
        background-color: #fafafa;
      }
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OpportunitiesTableComponent {
  private readonly api = inject(CrmApiService);

  protected displayedColumns = ['id', 'title', 'status', 'actions'];
  protected dataSource = new MatTableDataSource<OpportunityItem>([]);

  protected onSortChange(sort: Sort): void {
    // Implement sorting logic
  }

  protected onPageChange(event: PageEvent): void {
    // Implement pagination logic
  }
}
```

### 2. Formularios Material

**Componente TypeScript:**

```typescript
import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';

@Component({
  selector: 'app-opportunity-form',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatDatepickerModule,
    MatButtonModule,
    MatIconModule,
  ],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()" class="form-container">
      <!-- Title Field -->
      <mat-form-field appearance="outline" class="full-width">
        <mat-label>Título</mat-label>
        <input matInput formControlName="title" required />
        <mat-icon matPrefix>description</mat-icon>
        <mat-error *ngIf="form.get('title')?.hasError('required')">
          El título es requerido
        </mat-error>
      </mat-form-field>

      <!-- Category Select -->
      <mat-form-field appearance="outline" class="full-width">
        <mat-label>Categoría</mat-label>
        <mat-select formControlName="category">
          <mat-option value="engineering">Ingeniería</mat-option>
          <mat-option value="services">Servicios</mat-option>
          <mat-option value="supplies">Suministros</mat-option>
        </mat-select>
        <mat-icon matPrefix>category</mat-icon>
      </mat-form-field>

      <!-- Date Field -->
      <mat-form-field appearance="outline" class="full-width">
        <mat-label>Fecha de cierre</mat-label>
        <input matInput [matDatepicker]="picker" formControlName="dueDate" />
        <mat-datepicker-toggle matSuffix [for]="picker"></mat-datepicker-toggle>
        <mat-datepicker #picker></mat-datepicker>
      </mat-form-field>

      <!-- Submit Button -->
      <div class="button-group">
        <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid">
          <mat-icon>save</mat-icon>
          Guardar
        </button>
        <button mat-stroked-button type="button">Cancelar</button>
      </div>
    </form>
  `,
  styles: [`
    .form-container {
      display: flex;
      flex-direction: column;
      gap: 16px;
      max-width: 600px;
    }

    .full-width {
      width: 100%;
    }

    .button-group {
      display: flex;
      gap: 12px;
      justify-content: flex-end;
      margin-top: 16px;

      button {
        min-width: 120px;
      }
    }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OpportunityFormComponent {
  private readonly fb = inject(FormBuilder);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly form = this.fb.group({
    title: ['', Validators.required],
    category: ['', Validators.required],
    dueDate: ['', Validators.required],
  });

  protected submit(): void {
    if (this.form.valid) {
      // Submit logic
      this.snackBar.open('Oportunidad guardada exitosamente', 'Cerrar', { duration: 3000 });
    }
  }
}
```

### 3. Diálogos Material

```typescript
import { MatDialog, MatDialogModule } from '@angular/material/dialog';

// En el componente principal:
export class CommercialPageComponent {
  private readonly dialog = inject(MatDialog);

  protected openDetailDialog(opportunity: OpportunityDetail): void {
    this.dialog.open(OpportunityDetailDialogComponent, {
      width: '600px',
      maxHeight: '90vh',
      data: { opportunity },
      panelClass: 'opportunity-dialog',
    });
  }
}
```

### 4. Tarjetas de Resumen (Cards)

```html
<mat-card class="summary-card">
  <mat-card-header>
    <mat-card-title>
      <mat-icon>trending_up</mat-icon>
      Oportunidades Activas
    </mat-card-title>
  </mat-card-header>
  
  <mat-card-content>
    <div class="card-stats">
      <div class="stat">
        <span class="stat-value">{{ activeCount }}</span>
        <span class="stat-label">Total</span>
      </div>
      <div class="stat">
        <span class="stat-value">{{ pendingCount }}</span>
        <span class="stat-label">Pendientes</span>
      </div>
    </div>
  </mat-card-content>
</mat-card>
```

## Módulos a Modernizar

### FASE 4: Commercial Module
- **Archivo**: `frontend/src/app/features/commercial/`
- **Cambios clave**:
  - Aplicar patrón de tabla Material
  - Reemplazar filtros con mat-form-field + mat-select
  - Usar mat-chip para estados
  - Agregar mat-dialog para detalles

### FASE 5: Management Module
- **Archivo**: `frontend/src/app/features/management/`
- **Cambios clave**:
  - Crear KPI cards con mat-card
  - Integrar Chart.js para gráficos
  - Agregar date-range picker
  - Usar mat-image-list para reportes

### FASE 6: Operations Module
- **Archivo**: `frontend/src/app/features/operations/`
- **Cambios clave**:
  - Aplicar patrones de formulario Material
  - Usar mat-expansion-panel para secciones
  - Agregar mat-slide-toggle para configuraciones
  - Dialogs para confirmaciones

## Checklist de Modernización

- [ ] Módulo importa SharedModule (Material + Common)
- [ ] Todos los inputs usan mat-form-field + matInput
- [ ] Tablas usan mat-table + mat-sort + mat-paginator
- [ ] Botones usan mat-button / mat-icon-button
- [ ] Errores visibles con mat-error
- [ ] Loading states con mat-progress-spinner
- [ ] Notificaciones con MatSnackBar
- [ ] Diálogos usan MatDialog
- [ ] Componentes standalone importan Material
- [ ] Estilos responsive (mobile-first)
- [ ] ARIA labels en elementos interactivos
- [ ] Colores accesibles (WCAG AA)

## Compilar y Verificar

```bash
cd frontend
npm run build  # Compilación production
npm test       # Tests (actualizar templates si cambian)
```

## Próximos Pasos

1. Aplicar patrones a Commercial (FASE 4)
2. Aplicar patrones a Management (FASE 5)
3. Aplicar patrones a Operations (FASE 6)
4. Agregar media queries + accesibilidad (FASE 7)
5. Testing completo + Lighthouse validation

