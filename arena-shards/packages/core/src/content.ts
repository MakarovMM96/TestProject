import { z } from 'zod';
import creaturesJson from './data/creatures.json';
import runesJson from './data/runes.json';
import heroesJson from './data/heroes.json';
import contractsJson from './data/contracts.json';
import type { ContractDef, CreatureDef, HeroDef, RuneDef } from './types';

const effectSchema = z.object({
  trigger: z.string(),
  condition: z.record(z.string(), z.unknown()).optional(),
  action: z.string(),
  target: z.string(),
  params: z.record(z.string(), z.union([z.number(), z.string()])),
});

const creatureSchema = z.object({
  id: z.string(),
  name: z.string(),
  tribe: z.string(),
  tier: z.number().min(1).max(6),
  attack: z.number().min(0),
  health: z.number().min(1),
  row: z.string(),
  keywords: z.record(z.string(), z.union([z.number(), z.boolean()])),
  effects: z.array(effectSchema),
  awaken: z
    .object({ after: z.number(), effects: z.array(effectSchema) })
    .optional(),
  token: z.boolean().optional(),
});

const runeSchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string(),
  ranks: z.tuple([z.record(z.string(), z.unknown()), z.record(z.string(), z.unknown()), z.record(z.string(), z.unknown())]),
  backRowOnly: z.boolean().optional(),
});

const heroSchema = z.object({
  id: z.string(),
  name: z.string(),
  armor: z.number(),
  description: z.string(),
  power: z.object({
    kind: z.enum(['active', 'passive']),
    cost: z.number().optional(),
    action: z.string(),
    params: z.record(z.string(), z.union([z.number(), z.string()])).optional(),
  }),
});

const contractSchema = z.object({
  id: z.string(),
  name: z.string(),
  condition: z.string(),
  params: z.record(z.string(), z.union([z.number(), z.string()])).optional(),
  durationTurns: z.number(),
  reward: z.string(),
  rewardParams: z.record(z.string(), z.union([z.number(), z.string()])).optional(),
});

export const CREATURES: CreatureDef[] = z.array(creatureSchema).parse(creaturesJson) as unknown as CreatureDef[];
export const RUNES: RuneDef[] = z.array(runeSchema).parse(runesJson) as unknown as RuneDef[];
export const HEROES: HeroDef[] = z.array(heroSchema).parse(heroesJson) as unknown as HeroDef[];
export const CONTRACTS: ContractDef[] = z.array(contractSchema).parse(contractsJson) as unknown as ContractDef[];

export const CREATURES_BY_ID: Record<string, CreatureDef> = Object.fromEntries(
  CREATURES.map((c) => [c.id, c]),
);
export const RUNES_BY_ID: Record<string, RuneDef> = Object.fromEntries(RUNES.map((r) => [r.id, r]));
export const HEROES_BY_ID: Record<string, HeroDef> = Object.fromEntries(HEROES.map((h) => [h.id, h]));
export const CONTRACTS_BY_ID: Record<string, ContractDef> = Object.fromEntries(
  CONTRACTS.map((c) => [c.id, c]),
);

export const TAVERN_TABLE = [
  { tier: 1, upgradeCost: 0, shopSize: 3, copiesInPool: 16 },
  { tier: 2, upgradeCost: 5, shopSize: 4, copiesInPool: 15 },
  { tier: 3, upgradeCost: 7, shopSize: 4, copiesInPool: 13 },
  { tier: 4, upgradeCost: 8, shopSize: 5, copiesInPool: 11 },
  { tier: 5, upgradeCost: 9, shopSize: 5, copiesInPool: 9 },
  { tier: 6, upgradeCost: 11, shopSize: 6, copiesInPool: 7 },
] as const;

export const ALL_TRIBES = ['crystal', 'swamp', 'spark', 'ash', 'night', 'grove'] as const;
