'use client';

import React from 'react';

export type ToastType = 'success' | 'error';

export function Field({ label, value, onChange, placeholder, required, mono, type = 'text' }: {
  label: string; value: string; onChange: (v: string) => void; placeholder?: string; required?: boolean; mono?: boolean; type?: string;
}) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}{required && <span className="text-red-500"> *</span>}</label>
      <input type={type} required={required} value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder}
        className={`w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-azure-500 focus:border-azure-500 ${mono ? 'font-mono' : ''}`} />
    </div>
  );
}

export function ModalShell({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto p-6">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-gray-900">{title}</h2>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600">
            <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" /></svg>
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

export function ModalActions({ busy, submitLabel, onClose }: { busy: boolean; submitLabel: string; onClose: () => void }) {
  return (
    <div className="flex justify-end gap-3 pt-4 border-t border-gray-200">
      <button type="button" onClick={onClose} className="px-4 py-2 text-gray-700 border border-gray-300 rounded-lg text-sm hover:bg-gray-50">Cancel</button>
      <button type="submit" disabled={busy} className="px-4 py-2 bg-azure-600 text-white rounded-lg text-sm hover:bg-azure-700 disabled:opacity-50 flex items-center gap-2">
        {busy && <Spinner />}{busy ? 'Working...' : submitLabel}
      </button>
    </div>
  );
}

export function Spinner() { return <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-current" />; }

export function ErrorBox({ children }: { children: React.ReactNode }) {
  return <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-lg text-sm text-red-700">{children}</div>;
}

export function Toast({ toast, onClose }: { toast: { message: string; type: ToastType }; onClose: () => void }) {
  return (
    <div className={`fixed top-4 right-4 z-[60] max-w-sm px-4 py-3 rounded-lg shadow-lg text-sm font-medium flex items-center gap-2 ${
      toast.type === 'success' ? 'bg-green-50 border border-green-200 text-green-800' : 'bg-red-50 border border-red-200 text-red-800'}`}>
      <span>{toast.type === 'success' ? '✓' : '✕'}</span>
      <span className="flex-1">{toast.message}</span>
      <button onClick={onClose} className="ml-2 opacity-60 hover:opacity-100">✕</button>
    </div>
  );
}

export function AccessDenied() {
  return (
    <div className="text-center py-16 bg-white rounded-lg border border-gray-200 max-w-lg mx-auto">
      <p className="text-gray-700 font-medium mb-1">Access denied</p>
      <p className="text-sm text-gray-400">You don&apos;t have permission to view this page.</p>
    </div>
  );
}
