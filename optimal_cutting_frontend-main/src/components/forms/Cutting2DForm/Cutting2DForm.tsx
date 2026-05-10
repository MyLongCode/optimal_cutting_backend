import { Alert, Button, Flex, Form, InputNumber, Select } from 'antd';
import { TableTypes } from '../../../types/typeTable';
import { Table } from '../../custom-table/Table';
import { useMemo, useState } from 'react';
import { FormTabs, FormTabsType } from '../../FormTabs/FormTabs';
import { TabsOptions } from '../../FormTabs/tabsOption';
import { FormContainer } from '../../FormContainer/FormContainer';
import multiple from '../../../assets/icons/multiple.svg';
import styles from './Cutting2DForm.module.css';
import {
    useCalculate2DNestingFromDbMutation,
    useGetWorkpiecesQuery,
} from '../../../app/services/cutting2d';
import { useAppSelector } from '../../../app/hooks';
import { selectAddedDetails } from '../../../features/selectDetails2DSlice';
import { Designation } from '../../../app/services/addDxf';
import { Cutting2DNestingFromDbRequest } from '../../../types/Nesting2D';

const DEFAULT_SCALE = 1000;
const DEFAULT_ROTATIONS = [0, 90, 180, 270];

type SheetSize = {
    id: string;
    width: number;
    height: number;
};

type ManualSheetForm = {
    width: number;
    height: number;
};

const getRectangleSheet = ({ id, width, height }: SheetSize) => ({
    id,
    outer: [
        [
            { x: 0, y: 0 },
            { x: width, y: 0 },
            { x: width, y: height },
            { x: 0, y: height },
        ],
    ],
    holes: [],
});

const getDetailIdsByCounts = (
    details: Designation[],
    counts: Record<string, number | undefined>
) =>
    details.flatMap((detail, index) => {
        const count = Number(counts[`count_${index + 1}`] ?? 0);
        return Array.from({ length: count }, () => detail.id);
    });

export const Cutting2DForm = () => {
    const [formDetail] = Form.useForm();
    const [formManualSheet] = Form.useForm<ManualSheetForm>();
    const [selectedWorkpieceId, setSelectedWorkpieceId] = useState<number>();
    const [modeBlank, setModeBlank] = useState<TabsOptions>(TabsOptions.valueFirst);
    const [kerf, setKerf] = useState<number>(0);
    const [clearance, setClearance] = useState<number>(0);
    const [error, setError] = useState<string>('');
    const { data: workpieces = [], isLoading: isWorkpiecesLoading } =
        useGetWorkpiecesQuery();
    const [calculate2DNestingFromDb, { isLoading: isCalculating }] =
        useCalculate2DNestingFromDbMutation();
    const addedDetails = useAppSelector(selectAddedDetails);
    const selectedDetails = useMemo(
        () => Object.values(addedDetails).flat(),
        [addedDetails]
    );
    const selectedWorkpiece = workpieces.find(
        (workpiece) => workpiece.id === selectedWorkpieceId
    );

    const propsSelect: FormTabsType = {
        tabTitleFirst: 'Станд. заготовка',
        tabTitleSecond: 'Ввести размеры',
        setTab: setModeBlank,
        tab: modeBlank,
    };

    const buildSheet = async () => {
        if (modeBlank === TabsOptions.valueFirst) {
            if (!selectedWorkpiece) return null;

            return getRectangleSheet({
                id: selectedWorkpiece.id.toString(),
                width: selectedWorkpiece.width,
                height: selectedWorkpiece.height,
            });
        }

        const manualSheet = await formManualSheet.validateFields();
        return getRectangleSheet({
            id: 'custom-sheet-1',
            width: manualSheet.width,
            height: manualSheet.height,
        });
    };

    const handlerSubmit = async () => {
        setError('');
        try {
            if (selectedDetails.length === 0) {
                setError('Добавьте хотя бы одну деталь из базы данных.');
                return;
            }

            await formDetail.validateFields();
            const detailIds = getDetailIdsByCounts(
                selectedDetails,
                formDetail.getFieldsValue()
            );
            if (detailIds.length === 0) {
                setError('Укажите количество хотя бы для одной детали.');
                return;
            }

            const sheet = await buildSheet();
            if (!sheet) {
                setError('Выберите стандартную заготовку или введите размеры новой.');
                return;
            }

            const request: Cutting2DNestingFromDbRequest = {
                detailIds,
                sheets: [sheet],
                kerf,
                clearance,
                scale: DEFAULT_SCALE,
                enableLocalSearch: true,
                allowedRotationsDegrees: DEFAULT_ROTATIONS,
            };

            await calculate2DNestingFromDb(request).unwrap();
        } catch {
            setError('Проверьте заполнение формы и повторите расчет.');
        }
    };

    return (
        <Flex className={styles['cutting2D']}>
            <FormContainer>
                <Flex className='formgap'>
                    <h2>Детали</h2>
                    <Table typeTable={TableTypes.detail2D} form={formDetail} />
                    <h2 style={{ marginTop: '44px' }}>Заготовка</h2>
                    <FormTabs {...propsSelect}></FormTabs>
                    {modeBlank === TabsOptions.valueFirst && (
                        <Select
                            placeholder='Выбрать заготовку'
                            loading={isWorkpiecesLoading}
                            value={selectedWorkpieceId}
                            options={workpieces.map((workpiece) => ({
                                value: workpiece.id,
                                label: `${workpiece.name} (${workpiece.width} × ${workpiece.height})`,
                            }))}
                            onChange={setSelectedWorkpieceId}
                        ></Select>
                    )}
                    {modeBlank === TabsOptions.valueSecond && (
                        <Form
                            form={formManualSheet}
                            className={styles['cutting2D__form-wrapper']}
                        >
                            <Form.Item
                                name='width'
                                rules={[{ required: true, message: '' }]}
                            >
                                <InputNumber
                                    min={1}
                                    placeholder='Длина'
                                    className={styles['cutting2D__input']}
                                ></InputNumber>
                            </Form.Item>
                            <img src={multiple} />
                            <Form.Item
                                name='height'
                                rules={[{ required: true, message: '' }]}
                            >
                                <InputNumber
                                    min={1}
                                    placeholder='Ширина'
                                    className={styles['cutting2D__input']}
                                ></InputNumber>
                            </Form.Item>
                        </Form>
                    )}
                    <h2 style={{ marginTop: '44px' }}>Параметры реза</h2>
                    <Flex gap={14}>
                        <InputNumber
                            min={0}
                            value={kerf}
                            placeholder='Рез'
                            className={styles['cutting2D__input']}
                            onChange={(value) => setKerf(value ?? 0)}
                        ></InputNumber>
                        <InputNumber
                            min={0}
                            value={clearance}
                            placeholder='Зазор'
                            className={styles['cutting2D__input']}
                            onChange={(value) => setClearance(value ?? 0)}
                        ></InputNumber>
                    </Flex>
                    {error && <Alert message={error} type='error' showIcon />}
                    <Button
                        type='primary'
                        danger
                        className='bottom-btn'
                        loading={isCalculating}
                        onClick={handlerSubmit}
                    >
                        Создать схему
                    </Button>
                </Flex>
            </FormContainer>
        </Flex>
    );
};
