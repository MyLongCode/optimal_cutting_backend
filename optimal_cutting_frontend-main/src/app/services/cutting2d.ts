import { WorkpieceStandard } from '../../types/Calculated2D';
import {
    Cutting2DNestingFromDbRequest,
    Cutting2DNestingResult,
} from '../../types/Nesting2D';
import { api } from './api';

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
        }),
    }),
});

export const { useGetWorkpiecesQuery, useCalculate2DNestingFromDbMutation } =
    cutting2DApi;
export const {
    endpoints: { getWorkpieces, calculate2DNestingFromDb },
} = cutting2DApi;
