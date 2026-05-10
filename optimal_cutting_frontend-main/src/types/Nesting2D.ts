export type NestingPoint = {
    x: number;
    y: number;
};

export type NestingSheet = {
    id: string;
    outer: NestingPoint[][];
    holes?: NestingPoint[][][];
};

export type Cutting2DNestingFromDbRequest = {
    detailIds: number[];
    sheets: NestingSheet[];
    kerf: number;
    clearance: number;
    scale: number;
    enableLocalSearch: boolean;
    allowedRotationsDegrees: number[];
};

export type NestingOutputPoint = {
    x: number;
    y: number;
};

export type NestingPlacement = {
    partId: string;
    sheetId: string;
    x: number;
    y: number;
    rotation: number;
    contours: NestingOutputPoint[][];
};

export type Workpiece2D = {
    width: number;
    height: number;
    details: unknown[];
    usedArea: number;
    percentUsage: number;
    procentUsage?: number;
};

export type Cutting2DNestingResult = {
    sheets: string[];
    workpieces: Workpiece2D[];
    placedParts: NestingPlacement[];
    unplacedParts: string[];
    utilizationBySheet: Record<string, number>;
    totalUtilization: number;
    svg: string;
    dxf: string;
};
