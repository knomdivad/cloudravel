'use client';

import React, { useState, useRef, useEffect, useCallback } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useTenantContext } from '../../contexts/TenantContext';
import { queryAi } from '../../lib/api';
import type { AiQueryResponse, AiToolInvocation } from '../../lib/types';

interface ChatMessage {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  toolsUsed?: AiToolInvocation[];
  usage?: { promptTokens: number; completionTokens: number; totalTokens: number };
  timestamp: Date;
}

const MODES = [
  { key: 'analyst', label: 'Analyst', hint: 'General environment Q&A' },
  { key: 'operations', label: 'Operations', hint: 'Triage anomalies & incidents, propose remediations' },
  { key: 'security', label: 'Security', hint: 'Posture assessment & root cause' },
  { key: 'cost', label: 'Cost', hint: 'Waste & savings analysis' },
] as const;

const SUGGESTED_QUERIES: Record<string, string[]> = {
  analyst: [
    'What are the most critical security findings across all subscriptions?',
    'Show me resources without tags that should have them.',
    'List all changes to NSGs in the last 7 days.',
    'Are there any VMs running without managed identity?',
  ],
  operations: [
    'Triage the environment: what needs my attention right now?',
    'Investigate the open anomalies and propose remediations where a playbook fits.',
    'Which incidents are at risk of breaching SLA, and what has been tried so far?',
    'Summarize remediation activity from the last 7 days.',
  ],
  security: [
    'Assess the security posture and prioritize the top 5 remediation actions.',
    'Did any recent change explain the new critical findings?',
    'Which resources have security configuration drift in the last 24 hours?',
    'Show unusual actor activity and whether it touched security-sensitive resources.',
  ],
  cost: [
    'What are the top cost optimization opportunities?',
    'Which resources are over-provisioned based on SKU tier?',
    'Did identified waste increase recently, and which resources drive it?',
    'Find idle compute across Azure, AWS, and GCP.',
  ],
};

export default function AiPage() {
  const { tenantId, currentTenant } = useTenantContext();
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [conversationId] = useState(() => crypto.randomUUID());
  const [mode, setMode] = useState<string>('analyst');
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const sendMessage = useCallback(
    async (queryText?: string) => {
      const text = queryText ?? input.trim();
      if (!text || !tenantId || isLoading) return;

      const userMsg: ChatMessage = {
        id: crypto.randomUUID(),
        role: 'user',
        content: text,
        timestamp: new Date(),
      };

      setMessages((prev) => [...prev, userMsg]);
      setInput('');
      setIsLoading(true);

      try {
        const response: AiQueryResponse = await queryAi(tenantId, {
          query: text,
          conversationId,
          mode,
        });

        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: 'assistant',
          content: response.response,
          toolsUsed: response.toolsUsed,
          usage: response.usage,
          timestamp: new Date(),
        };

        setMessages((prev) => [...prev, assistantMsg]);
      } catch (err: any) {
        const errorMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: 'system',
          content: `Error: ${err.message || 'Failed to get AI response. Please try again.'}`,
          timestamp: new Date(),
        };
        setMessages((prev) => [...prev, errorMsg]);
      } finally {
        setIsLoading(false);
        inputRef.current?.focus();
      }
    },
    [input, tenantId, isLoading, conversationId, mode]
  );

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  };

  if (!tenantId) {
    return (
      <div className="text-center py-20 text-gray-500">Select a tenant to use AI Insights.</div>
    );
  }

  return (
    <div className="flex flex-col h-[calc(100vh-8rem)]">
      {/* Header */}
      <div className="mb-4 flex items-end justify-between gap-4">
        <div>
          <h1 className="text-xl font-bold">AI Insights</h1>
          <p className="text-sm text-gray-500 mt-1">
            Ask questions about {currentTenant?.displayName}&apos;s multi-cloud environment.
            Answers are grounded in real inventory data — never fabricated.
          </p>
        </div>
        <div className="flex gap-1 bg-gray-100 rounded-lg p-1 flex-shrink-0">
          {MODES.map((m) => (
            <button
              key={m.key}
              onClick={() => setMode(m.key)}
              title={m.hint}
              className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
                mode === m.key ? 'bg-white text-azure-700 shadow-sm' : 'text-gray-500 hover:text-gray-700'
              }`}
            >
              {m.label}
            </button>
          ))}
        </div>
      </div>

      {/* Chat area */}
      <div className="flex-1 bg-white rounded-xl shadow-sm border border-gray-200 flex flex-col overflow-hidden">
        <div className="flex-1 overflow-y-auto p-5 space-y-4">
          {messages.length === 0 && (
            <div className="flex flex-col items-center justify-center h-full text-center">
              <div className="w-12 h-12 bg-azure-100 rounded-full flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-azure-600" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09ZM18.259 8.715 18 9.75l-.259-1.035a3.375 3.375 0 0 0-2.455-2.456L14.25 6l1.036-.259a3.375 3.375 0 0 0 2.455-2.456L18 2.25l.259 1.035a3.375 3.375 0 0 0 2.456 2.456L21.75 6l-1.035.259a3.375 3.375 0 0 0-2.456 2.456ZM16.894 20.567 16.5 21.75l-.394-1.183a2.25 2.25 0 0 0-1.423-1.423L13.5 18.75l1.183-.394a2.25 2.25 0 0 0 1.423-1.423l.394-1.183.394 1.183a2.25 2.25 0 0 0 1.423 1.423l1.183.394-1.183.394a2.25 2.25 0 0 0-1.423 1.423Z" />
                </svg>
              </div>
              <h3 className="text-lg font-semibold text-gray-700 mb-2">
                Multi-Cloud Environment Intelligence
              </h3>
              <p className="text-sm text-gray-500 mb-6 max-w-md">
                Ask me about your inventory, changes, security findings, or recommendations.
                I use real-time data queries — never guessing.
              </p>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-2 max-w-2xl w-full">
                {(SUGGESTED_QUERIES[mode] ?? SUGGESTED_QUERIES.analyst).map((q, i) => (
                  <button
                    key={i}
                    onClick={() => sendMessage(q)}
                    className="text-left text-sm px-4 py-3 rounded-lg border border-gray-200 hover:border-azure-300 hover:bg-azure-50 transition-colors"
                  >
                    {q}
                  </button>
                ))}
              </div>
            </div>
          )}

          {messages.map((msg) => (
            <MessageBubble key={msg.id} message={msg} />
          ))}

          {isLoading && (
            <div className="flex items-start gap-3">
              <div className="w-8 h-8 bg-azure-100 rounded-full flex items-center justify-center flex-shrink-0">
                <svg className="w-4 h-4 text-azure-600 animate-pulse" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09Z" />
                </svg>
              </div>
              <div className="bg-gray-100 rounded-xl px-4 py-3 text-sm text-gray-600">
                <div className="flex items-center gap-2">
                  <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-azure-600" />
                  Querying your cloud environment...
                </div>
              </div>
            </div>
          )}

          <div ref={messagesEndRef} />
        </div>

        {/* Input area */}
        <div className="border-t border-gray-200 p-4">
          <div className="flex gap-3">
            <textarea
              ref={inputRef}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Ask about your cloud inventory, changes, or security..."
              rows={1}
              disabled={isLoading}
              className="flex-1 border border-gray-300 rounded-xl px-4 py-3 text-sm resize-none focus:border-azure-500 focus:outline-none focus:ring-1 focus:ring-azure-500 disabled:opacity-50"
              style={{ minHeight: '44px', maxHeight: '120px' }}
            />
            <button
              onClick={() => sendMessage()}
              disabled={!input.trim() || isLoading}
              className="px-5 py-3 bg-azure-600 text-white rounded-xl text-sm font-medium hover:bg-azure-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex-shrink-0"
            >
              Send
            </button>
          </div>
          <p className="text-xs text-gray-400 mt-2 text-center">
            AI responses are grounded in your tenant data via tool calls. No hallucinated facts.
          </p>
        </div>
      </div>
    </div>
  );
}

function MessageBubble({ message }: { message: ChatMessage }) {
  if (message.role === 'user') {
    return (
      <div className="flex items-start gap-3 justify-end">
        <div className="bg-azure-600 text-white rounded-xl px-4 py-3 text-sm max-w-2xl">
          {message.content}
        </div>
        <div className="w-8 h-8 bg-gray-200 rounded-full flex items-center justify-center flex-shrink-0 text-xs font-medium">
          U
        </div>
      </div>
    );
  }

  if (message.role === 'system') {
    return (
      <div className="text-center">
        <span className="text-xs text-red-500 bg-red-50 px-3 py-1 rounded-full">{message.content}</span>
      </div>
    );
  }

  return (
    <div className="flex items-start gap-3">
      <div className="w-8 h-8 bg-azure-100 rounded-full flex items-center justify-center flex-shrink-0">
        <svg className="w-4 h-4 text-azure-600" fill="none" viewBox="0 0 24 24" strokeWidth="1.5" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09Z" />
        </svg>
      </div>
      <div className="max-w-2xl space-y-2">
        <div className="bg-gray-100 rounded-xl px-4 py-3 text-sm text-gray-800 prose prose-sm prose-gray max-w-none [&_table]:w-full [&_table]:text-xs [&_th]:bg-gray-200 [&_th]:px-3 [&_th]:py-1.5 [&_th]:text-left [&_th]:font-semibold [&_td]:px-3 [&_td]:py-1.5 [&_td]:border-t [&_td]:border-gray-200 [&_pre]:bg-gray-200 [&_pre]:rounded-lg [&_code]:text-xs [&_p:last-child]:mb-0">
          <ReactMarkdown remarkPlugins={[remarkGfm]}>{message.content}</ReactMarkdown>
        </div>

        {/* Tool usage transparency */}
        {message.toolsUsed && message.toolsUsed.length > 0 && (
          <details className="text-xs text-gray-400">
            <summary className="cursor-pointer hover:text-gray-600">
              Used {message.toolsUsed.length} tool{message.toolsUsed.length > 1 ? 's' : ''} —{' '}
              {message.usage && `${message.usage.totalTokens} tokens`}
            </summary>
            <div className="mt-1 space-y-0.5 pl-2 border-l-2 border-gray-200">
              {message.toolsUsed.map((t, i) => (
                <div key={i} className="font-mono">
                  {t.toolName}({t.arguments?.slice(0, 80)}) — {t.durationMs}ms
                </div>
              ))}
            </div>
          </details>
        )}
      </div>
    </div>
  );
}
