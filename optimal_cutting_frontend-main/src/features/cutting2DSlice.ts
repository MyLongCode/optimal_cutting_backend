import { createSlice } from '@reduxjs/toolkit';
import { cutting2DApi } from '../app/services/cutting2d';
import { RootState } from '../app/store';
import { Cutting2DNestingResult } from '../types/Nesting2D';

const initialState: Cutting2DNestingResult = {
    sheets: [],
    workpieces: [],
    placedParts: [],
    unplacedParts: [],
    utilizationBySheet: {},
    totalUtilization: 0,
    svg: '',
    dxf: '',
};

const slice = createSlice({
    name: 'cutting2D',
    initialState,
    reducers: {},
    extraReducers: (builder) => {
        builder.addMatcher(
            cutting2DApi.endpoints.calculate2DNestingFromDb.matchFulfilled,
            (_state, action) => action.payload
        );
    },
});

export default slice.reducer;
export const selectCalculateData2D = (state: RootState) => state.cutting2D;
