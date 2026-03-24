import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { App } from './app';

describe('App', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    flushInitialLoad(httpMock);

    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render the CRM title', () => {
    const fixture = TestBed.createComponent(App);
    flushInitialLoad(httpMock);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('HDM Procurement Cockpit');
  });
});

function flushInitialLoad(httpMock: HttpTestingController): void {
  const requests = httpMock.match(() => true);
  expect(requests.length).toBe(8);

  for (const request of requests) {
    switch (request.request.url) {
      case '/api/meta':
        request.flush({
          n8nEditorUrl: 'http://localhost:5678/',
          storageMode: 'PostgreSQL local + CRM',
          storageTarget: 'sercop_crm',
          responsibleEmail: 'gerencia@example.com',
          invitedCompanyName: 'HDM',
        });
        break;
      case '/api/dashboard':
        request.flush({
          totalOpportunities: 0,
          invitedOpportunities: 0,
          assignedOpportunities: 0,
          unassignedOpportunities: 0,
          activeZones: 0,
          activeUsers: 0,
          workflowCount: 0,
          statuses: [],
          zoneLoads: [],
        });
        break;
      case '/api/management/report':
        request.flush({
          summary: {
            totalVisibleOpportunities: 0,
            assignedOpportunities: 0,
            participatingOpportunities: 0,
            wonOpportunities: 0,
            lostOpportunities: 0,
            notPresentedOpportunities: 0,
            activeSellers: 0,
            overallHitRatePercent: 0,
            totalWonAmount: 0,
            salesShareBasis: 'procesos_ganados',
          },
          pipeline: [],
          sellers: [],
          winningAreas: [],
        });
        break;
      case '/api/zones':
      case '/api/users':
      case '/api/keywords':
      case '/api/workflows':
        request.flush([]);
        break;
      case '/api/opportunities':
        request.flush([]);
        break;
      default:
        fail(`Unexpected request: ${request.request.method} ${request.request.urlWithParams}`);
    }
  }
}
