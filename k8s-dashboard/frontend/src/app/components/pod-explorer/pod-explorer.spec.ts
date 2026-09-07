import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { PodExplorer } from './pod-explorer';

describe('PodExplorer', () => {
  let component: PodExplorer;
  let fixture: ComponentFixture<PodExplorer>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PodExplorer],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    }).compileComponents();

    fixture = TestBed.createComponent(PodExplorer);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
