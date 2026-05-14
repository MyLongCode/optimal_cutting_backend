import { WorkpieceStandard } from '../../types/Calculated2D';
import {
    Cutting2DNestingFromDbRequest,
    Cutting2DNestingResult,
    NestingPlacement,
    Workpiece2D,
} from '../../types/Nesting2D';
import { api } from './api';

type RawNestingPlacement = Partial<NestingPlacement> & {
    PartId?: string;
    SheetId?: string;
    X?: number;
    Y?: number;
    Rotation?: number;
    Contours?: NestingPlacement['contours'];
};

type RawWorkpiece2D = Partial<Workpiece2D> & {
    Width?: number;
    Height?: number;
    Details?: unknown[];
    UsedArea?: number;
    PercentUsage?: number;
    ProcentUsage?: number;
};

type RawCutting2DNestingResult = Partial<Cutting2DNestingResult> & {
    Sheets?: string[];
    Workpieces?: RawWorkpiece2D[];
    PlacedParts?: RawNestingPlacement[];
    UnplacedParts?: string[];
    UtilizationBySheet?: Record<string, number>;
    TotalUtilization?: number;
    Svg?: string;
    Dxf?: string;
    diagnostics?: unknown;
    Diagnostics?: unknown;
};

const normalizePlacement = (placement: RawNestingPlacement): NestingPlacement => ({
    partId: placement.partId ?? placement.PartId ?? '',
    sheetId: placement.sheetId ?? placement.SheetId ?? '',
    x: placement.x ?? placement.X ?? 0,
    y: placement.y ?? placement.Y ?? 0,
    rotation: placement.rotation ?? placement.Rotation ?? 0,
    contours: placement.contours ?? placement.Contours ?? [],
});

const normalizeWorkpiece = (workpiece: RawWorkpiece2D): Workpiece2D => ({
    width: workpiece.width ?? workpiece.Width ?? 0,
    height: workpiece.height ?? workpiece.Height ?? 0,
    details: workpiece.details ?? workpiece.Details ?? [],
    usedArea: workpiece.usedArea ?? workpiece.UsedArea ?? 0,
    percentUsage:
        workpiece.percentUsage ??
        workpiece.PercentUsage ??
        workpiece.procentUsage ??
        workpiece.ProcentUsage ??
        0,
});

const normalizeNestingResult = (
    response: RawCutting2DNestingResult
): Cutting2DNestingResult => ({
    sheets: response.sheets ?? response.Sheets ?? [],
    workpieces: (response.workpieces ?? response.Workpieces ?? []).map(normalizeWorkpiece),
    placedParts: (response.placedParts ?? response.PlacedParts ?? []).map(
        normalizePlacement
    ),
    unplacedParts: response.unplacedParts ?? response.UnplacedParts ?? [],
    utilizationBySheet: response.utilizationBySheet ?? response.UtilizationBySheet ?? {},
    totalUtilization: response.totalUtilization ?? response.TotalUtilization ?? 0,
    svg: response.svg ?? response.Svg ?? '',
    dxf: response.dxf ?? response.Dxf ?? '',
    diagnostics: response.diagnostics ?? response.Diagnostics,
    responseKeys: Object.keys(response),
});

export const cutting2DApi = api.injectEndpoints({
    endpoints: (builder) => ({
        getWorkpieces: builder.query<WorkpieceStandard[], void>({
            query: () => ({
                url: '/detail/workpiece',
                method: 'GET',
            }),
        }),
        calculate2DNestingFromDb: builder.mutation<
            Cutting2DNestingResult,
            Cutting2DNestingFromDbRequest
        >({
            query: (data) => ({
                url: '/cutting2d/nesting/from-db',
                method: 'POST',
                body: data,
            }),
            transformResponse: (response: RawCutting2DNestingResult) =>
                normalizeNestingResult(response),
        }),
    }),
});

export const { useGetWorkpiecesQuery, useCalculate2DNestingFromDbMutation } =
    cutting2DApi;
export const {
    endpoints: { getWorkpieces, calculate2DNestingFromDb },
} = cutting2DApi;
